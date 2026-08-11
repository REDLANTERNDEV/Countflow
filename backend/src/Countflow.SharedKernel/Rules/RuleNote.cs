namespace Countflow.SharedKernel.Rules;

/// <summary>
/// Why a rule reached the verdict it did, as a code and its arguments.
/// <para>
/// Not prose, for the same reason <see cref="Results.Error"/> is not prose: this
/// text ends up in front of an operator holding a scanner, and the wording lives
/// in <c>packages/i18n</c> so a Turkish handheld says it in Turkish without the
/// backend shipping translations. A rule that returned a sentence would be a rule
/// only half the warehouse could read.
/// </para>
/// <para>
/// Arguments are <b>named</b>, matching the shape <c>/api/scan/resolve</c>
/// already uses. Positional arguments would be shorter to write and wrong:
/// Turkish puts its clauses in a different order, so a translator has to be able
/// to move <c>{{required}}</c> in front of <c>{{free}}</c> — which they cannot do
/// if the two are only distinguishable by index.
/// </para>
/// <para>
/// Values are stringified where they are captured. A trace is a record of what
/// was said, and it has to still say the same thing when it is read back in six
/// months — which means the value has to survive storage without depending on the
/// reader's locale.
/// </para>
/// </summary>
public sealed record RuleNote(string Code, IReadOnlyDictionary<string, string> Args)
{
    private static readonly IReadOnlyDictionary<string, string> NoArgs = new Dictionary<string, string>();

    public static RuleNote Of(string code) => new(code, NoArgs);

    public static RuleNote Of(string code, params (string Key, string Value)[] args) =>
        new(code, args.ToDictionary(arg => arg.Key, arg => arg.Value, StringComparer.Ordinal));
}
