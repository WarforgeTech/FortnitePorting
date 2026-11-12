using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FortnitePorting.Mcp.Core;

/// <summary>
/// In-process, reflection-driven invocation of any [McpServerTool] in this assembly.
///
/// This exists purely so `--call` can exercise tools without a JSON-RPC round trip. It deliberately
/// knows nothing about individual tool classes: every [McpServerToolType] in the assembly is picked
/// up automatically, so tools added by other work packages are callable the moment they compile.
/// Parameter binding mirrors the SDK: DI-satisfiable types and CancellationToken come from the
/// container, everything else from the JSON arguments.
/// </summary>
public static class ToolDispatcher
{
    public sealed record ToolBinding(string Name, MethodInfo Method, Type DeclaringType);

    public static IReadOnlyList<ToolBinding> Discover(Assembly? assembly = null)
    {
        assembly ??= typeof(ToolDispatcher).Assembly;

        var bindings = new List<ToolBinding>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute) continue;
                bindings.Add(new ToolBinding(attribute.Name ?? DeriveName(method.Name), method, type));
            }
        }

        return bindings.OrderBy(binding => binding.Name, StringComparer.Ordinal).ToList();
    }

    public static async Task<CallToolResult> InvokeAsync(
        IServiceProvider services, string toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var bindings = Discover();
        var binding = bindings.FirstOrDefault(b => b.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new McpException($"No tool named \"{toolName}\". Known tools: {string.Join(", ", bindings.Select(b => b.Name))}");

        object? target = null;
        if (!binding.Method.IsStatic)
            target = ActivatorUtilities.CreateInstance(services, binding.DeclaringType);

        var parameters = binding.Method.GetParameters();
        var values = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
            values[i] = BindParameter(services, parameters[i], arguments, cancellationToken, toolName);

        var returned = binding.Method.Invoke(target, values);
        var awaited = await UnwrapAsync(returned);
        return ToCallToolResult(awaited);
    }

    private static object? BindParameter(
        IServiceProvider services, ParameterInfo parameter, JsonElement? arguments, CancellationToken cancellationToken, string toolName)
    {
        var type = parameter.ParameterType;

        if (type == typeof(CancellationToken)) return cancellationToken;
        if (type == typeof(IServiceProvider)) return services;

        // DI first, exactly like the SDK: such parameters are hidden from the tool's input schema.
        if (!IsJsonBindable(type) && services.GetService(type) is { } resolved) return resolved;

        var name = parameter.Name ?? string.Empty;
        if (arguments is { ValueKind: JsonValueKind.Object } argsObject &&
            TryGetProperty(argsObject, name, out var element) &&
            element.ValueKind is not JsonValueKind.Null)
        {
            try
            {
                return JsonSerializer.Deserialize(element.GetRawText(), type, JsonOptions);
            }
            catch (JsonException e)
            {
                throw new McpException($"Argument \"{name}\" of {toolName} could not be read as {type.Name}: {e.Message}");
            }
        }

        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null) return null;
        if (services.GetService(type) is { } lateResolved) return lateResolved;

        throw new McpException($"Missing required argument \"{name}\" for {toolName}.");
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Primitives and collections always come from the JSON arguments, never from DI.</summary>
    private static bool IsJsonBindable(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
               || underlying.IsEnum
               || underlying == typeof(string)
               || underlying == typeof(decimal)
               || underlying == typeof(Guid)
               || underlying == typeof(DateTime)
               || underlying == typeof(DateTimeOffset)
               || underlying.IsArray;
    }

    private static async Task<object?> UnwrapAsync(object? returned)
    {
        switch (returned)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                return taskType.IsGenericType ? taskType.GetProperty("Result")!.GetValue(task) : null;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
            default:
                var type = returned.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task) type.GetMethod("AsTask")!.Invoke(returned, null)!;
                    await asTask.ConfigureAwait(false);
                    return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
                }

                return returned;
        }
    }

    private static CallToolResult ToCallToolResult(object? value) => value switch
    {
        null => new CallToolResult { Content = [] },
        CallToolResult result => result,
        ContentBlock block => new CallToolResult { Content = [block] },
        IEnumerable<ContentBlock> blocks => new CallToolResult { Content = blocks.ToList() },
        string text => new CallToolResult { Content = [new TextContentBlock { Text = text }] },
        _ => new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonOptions) }]
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string DeriveName(string methodName)
    {
        if (methodName.EndsWith("Async", StringComparison.Ordinal))
            methodName = methodName[..^5];

        var builder = new StringBuilder();
        for (var i = 0; i < methodName.Length; i++)
        {
            var c = methodName[i];
            if (char.IsUpper(c))
            {
                if (i > 0) builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
