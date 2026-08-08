using System.Globalization;

namespace AgMemory.Contracts;

/// <summary>Normalizes opaque IDs without inferring authorization from their format.</summary>
public static class OpaqueValue
{
    public static string Normalize(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return Guid.TryParse(normalized, out var guid)
            ? guid.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant()
            : normalized;
    }

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value);

    public static void Require(string? value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
}

public readonly record struct ScopeId
{
    public ScopeId(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
    public static implicit operator string(ScopeId value) => value.Value;
}

public readonly record struct MemoryId
{
    public MemoryId(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
    public static implicit operator string(MemoryId value) => value.Value;
}

public readonly record struct CommandId
{
    public CommandId(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
}

public readonly record struct ActorId
{
    public ActorId(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
}

public readonly record struct CorrelationId
{
    public CorrelationId(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
}

public readonly record struct ContractVersion
{
    public ContractVersion(string value) => Value = OpaqueValue.Normalize(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
    public void Validate(string parameterName) => OpaqueValue.Require(Value, parameterName);
}
