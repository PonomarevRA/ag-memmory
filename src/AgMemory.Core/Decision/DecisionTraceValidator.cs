using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class DecisionTraceValidator
{
    private const int MaximumFieldCharacters = 512;
    private const int MaximumListEntries = 8;
    private const int MaximumTotalCharacters = 2_048;

    public static void Validate(DecisionDetails trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        trace.Validate();
        ValidateField(trace.Problem, nameof(trace.Problem));
        ValidateField(trace.Context, nameof(trace.Context));
        ValidateField(trace.Decision, nameof(trace.Decision));
        ValidateField(trace.Reason, nameof(trace.Reason));
        if (trace.Outcome is not null) ValidateField(trace.Outcome, nameof(trace.Outcome));
        ValidateEntries(trace.Options, nameof(trace.Options));
        ValidateEntries(trace.Consequences, nameof(trace.Consequences));
        var total = trace.Problem.Length + trace.Context.Length + trace.Decision.Length + trace.Reason.Length +
            (trace.Outcome?.Length ?? 0) + trace.Options.Sum(option => option.Length) +
            trace.Consequences.Sum(consequence => consequence.Length);
        if (total > MaximumTotalCharacters)
            throw new ArgumentOutOfRangeException(nameof(trace), "A decision trace must remain compact.");
    }

    public static DecisionDetails Canonicalize(DecisionDetails trace) => trace with
    {
        Problem = CommandValueSupport.Canonicalize(trace.Problem),
        Context = CommandValueSupport.Canonicalize(trace.Context),
        Options = trace.Options.Select(CommandValueSupport.Canonicalize).ToArray(),
        Decision = CommandValueSupport.Canonicalize(trace.Decision),
        Reason = CommandValueSupport.Canonicalize(trace.Reason),
        Consequences = trace.Consequences.Select(CommandValueSupport.Canonicalize).ToArray(),
        Outcome = trace.Outcome is null ? null : CommandValueSupport.Canonicalize(trace.Outcome)
    };

    private static void ValidateEntries(IReadOnlyList<string> values, string parameterName)
    {
        if (values.Count > MaximumListEntries)
            throw new ArgumentOutOfRangeException(parameterName, "A decision trace must have a bounded number of list entries.");
        foreach (var value in values) ValidateField(value, parameterName);
    }

    private static void ValidateField(string value, string parameterName)
    {
        if (value.Length > MaximumFieldCharacters)
            throw new ArgumentOutOfRangeException(parameterName, "A decision trace field must remain compact.");
        if (value.Any(char.IsControl))
            throw new ArgumentException("A decision trace cannot contain transcript or log-shaped control characters.", parameterName);
    }
}
