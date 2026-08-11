using System.Globalization;
using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Rules;

namespace Countflow.Modules.Rules.CycleCount;

/// <summary>
/// The hard constraints on what may be counted.
/// <para>
/// Fewer than putaway's seven or allocation's six, and that is the honest number
/// rather than an unfinished one. Almost nothing disqualifies a holding from being
/// counted — that is the whole nature of the decision, since the point is to verify
/// what nobody has verified. What these four rule out is not stock that must not be
/// counted but counts that would <i>tell you nothing</i>: a bay mid-putaway, a bay
/// somebody is already walking to, a bay checked on Tuesday.
/// </para>
/// </summary>
internal static class CountConstraints
{
    /// <summary>
    /// Stock has to be somewhere a counter can be sent and asked a meaningful
    /// question.
    /// <para>
    /// First, because it is the cheapest and because the answer it gives is the
    /// most complete. A pack bench holds a half-built order and a dock holds a
    /// trailer half unloaded; both are full of stock, both would count "wrong" every
    /// time, and both would be wrong about being wrong. Roles are a parameter so a
    /// warehouse that genuinely counts its staging lanes can say so without a
    /// deploy.
    /// </para>
    /// </summary>
    internal sealed class CountableLocation(IReadOnlyCollection<StorageRole> roles)
        : IConstraintRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.countable_location";

        /// <summary>
        /// Reserve, forward and hold. Staging is left out by default and is the
        /// interesting omission: it holds real stock that is genuinely worth
        /// verifying, and it holds it for twenty minutes at a time, so a count
        /// raised against it is usually obsolete before anybody reaches it.
        /// </summary>
        public static readonly IReadOnlyCollection<StorageRole> DefaultRoles =
            [StorageRole.Reserve, StorageRole.Forward, StorageRole.Hold];

        public string Code => RuleCode;

        public RuleNote? Exclude(CountCandidate candidate, CountContext context)
        {
            var location = context.LocationOf(candidate);

            if (location is null)
            {
                return RuleNote.Of("rules.count.location_unknown");
            }

            return roles.Contains(location.Role)
                ? null
                : RuleNote.Of("rules.count.not_countable", ("role", location.Role.ToString()));
        }
    }

    /// <summary>
    /// The stock has to have stopped moving.
    /// <para>
    /// A bay a putaway landed in ten minutes ago is not a bay whose record is in
    /// doubt; it is a bay whose record was written ten minutes ago by the same
    /// system doing the doubting. Counting it measures the truck. Worse, it
    /// manufactures variances — a pallet part-way through being broken down will
    /// disagree with the book at almost any instant during the operation, and every
    /// one of those disagreements would be posted as a real adjustment and would
    /// then have to be corrected back.
    /// </para>
    /// <para>
    /// Hours rather than a work-in-progress flag, because there is no such flag and
    /// inventing one would mean the ledger telling the counting policy what it is
    /// in the middle of. The projection already records when it was last written to,
    /// which is the same fact arrived at without a new concept.
    /// </para>
    /// </summary>
    internal sealed class SettledStock(int quietPeriodHours)
        : IConstraintRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.settled_stock";

        public const int DefaultQuietPeriodHours = 4;

        public string Code => RuleCode;

        public RuleNote? Exclude(CountCandidate candidate, CountContext context)
        {
            var quiet = context.HoursSinceMovement(candidate);

            return quiet >= quietPeriodHours
                ? null
                : RuleNote.Of(
                    "rules.count.recently_moved",
                    ("hours", Math.Round(quiet, 1).ToString("0.#", CultureInfo.InvariantCulture)),
                    ("required", quietPeriodHours.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Somebody is already on their way there.
    /// <para>
    /// The list comes from the module that raised the outstanding counts, because
    /// that module owns the fact and this one cannot read its tables (ADR 0002).
    /// Passed in and excluded by a named rule rather than filtered out of the
    /// candidate query, so a supervisor asking why a badly overdue bay was skipped
    /// is told that it was not skipped — it is on somebody's handheld right now.
    /// </para>
    /// </summary>
    internal sealed class NotAlreadyScheduled : IConstraintRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.not_already_scheduled";

        public string Code => RuleCode;

        public RuleNote? Exclude(CountCandidate candidate, CountContext context) =>
            context.AlreadyScheduled.Contains(candidate.Id)
                ? RuleNote.Of("rules.count.already_scheduled")
                : null;
    }

    /// <summary>
    /// Leave alone what was checked recently.
    /// <para>
    /// The anti-churn rule, and the one that stops a counting policy eating itself.
    /// Without it, a holding that has just been counted and adjusted is a holding
    /// that has just moved and is therefore freshly interesting to half the
    /// preferences below — so the same bay would come back round every planning run
    /// while the far end of the building went unvisited for a year.
    /// </para>
    /// <para>
    /// Deliberately a flat minimum rather than the per-band target interval the
    /// overdue preference uses. They are answering different questions: this one is
    /// "is there any point?", which has the same answer for a fast mover and a slow
    /// one, while the preference is "how badly is this owed?", which does not. Tying
    /// them together would mean a C-class holding could never be counted twice in
    /// six months even when somebody had a reason.
    /// </para>
    /// </summary>
    internal sealed class NotRecentlyCounted(int minimumDaysBetweenCounts)
        : IConstraintRule<CountCandidate, CountContext>
    {
        public const string RuleCode = "count.not_recently_counted";

        public const int DefaultMinimumDays = 7;

        public string Code => RuleCode;

        public RuleNote? Exclude(CountCandidate candidate, CountContext context)
        {
            if (context.DaysSinceCount(candidate) is not { } days)
            {
                // Never counted. Nothing to be too recent.
                return null;
            }

            return days >= minimumDaysBetweenCounts
                ? null
                : RuleNote.Of(
                    "rules.count.counted_recently",
                    ("days", days.ToString(CultureInfo.InvariantCulture)),
                    ("required", minimumDaysBetweenCounts.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
