using System.Collections.Generic;
using Overworked.Documents;
using UnityEngine;

namespace Overworked.Npc
{
    /// <summary>
    /// Turns a list of "how many of what" into documents, and the rows that ask for them.
    /// </summary>
    /// <remarks>
    /// **Every document in a round is created here, in lockstep for every team.** That is a
    /// consequence of how the game is played rather than a rule: players print documents rather
    /// than naming them, so the only thing that ever names one is an NPC saying what it wants, and
    /// a request is a name — "Excel 1" — that both teams have their own copy of. Creating one
    /// team's copy and not the other's would leave the second team asked for something that does
    /// not exist, and the failure would only appear when a delivery mysteriously would not count.
    ///
    /// **The numbers coming out equal is checked rather than assumed.** It follows from the
    /// numbering being per (kind, team) and both copies being created in the same order — but the
    /// check costs one comparison and the failure it catches is invisible until it is expensive.
    /// See <see cref="DocumentStore.NextNumber"/> for why the count is a scan and therefore cannot
    /// drift on its own; this is here for the day something else creates a document as well.
    ///
    /// **This is one function because it was about to be two.** The customer spawner had it, and a
    /// colleague needs the same thing for both halves of a trade. The second copy is the one that
    /// drops the check, and what it drops it on is the invariant the whole round rests on.
    /// </remarks>
    public static class RequestWriter
    {
        /// <summary>
        /// Names every document the entries ask for, once per team, and appends the rows.
        /// </summary>
        /// <remarks>
        /// Appends to <paramref name="rows"/> rather than clearing it, so a caller building up more
        /// than one group can. Server only, and not marked as such because it is a static method on
        /// a static class: <see cref="DocumentStore.ServerCreate"/> carries its own guard.
        /// </remarks>
        /// <param name="entries">What is being asked for, as a count of each kind.</param>
        /// <param name="store">Where the documents are created.</param>
        /// <param name="teamCount">How many teams get a copy of each.</param>
        /// <param name="rows">Appended to, one row per requested document.</param>
        /// <param name="context">Whose fault it is, for the console. May be null.</param>
        public static void Name(
            RequestCatalogue.RequestEntry[] entries,
            DocumentStore store,
            int teamCount,
            List<DocumentRequest> rows,
            Object context)
        {
            if (entries == null || store == null || rows == null)
                return;

            for (int i = 0; i < entries.Length; i++)
            {
                RequestCatalogue.RequestEntry entry = entries[i];

                /* One entry asking for three of something is three rows — "Excel 1", "Excel 2",
                 * "Excel 3" — because that is how the request talks about them and how a player
                 * reads a folder against it. */
                for (int n = 0; n < entry.Count; n++)
                {
                    int number = -1;

                    for (int team = 0; team < teamCount; team++)
                    {
                        int id = store.ServerCreate(entry.SpecIndex, team);
                        store.TryGet(id, out DocumentRecord record);

                        if (team == 0)
                        {
                            number = record.Number;
                            continue;
                        }

                        if (record.Number != number)
                        {
                            Debug.LogError(
                                $"{nameof(RequestWriter)} for {(context != null ? context.name : "a request")}: " +
                                $"team {team} was given number {record.Number} for spec {entry.SpecIndex} where " +
                                $"team 0 was given {number}. The teams have drifted apart, and deliveries will " +
                                "stop matching.",
                                context);
                        }
                    }

                    /* The row is the name, not the document. Both teams' copies answer to it, and
                     * which one satisfies it is decided by the team doing the delivering — see
                     * Customer.Meets. */
                    rows.Add(new DocumentRequest { SpecIndex = entry.SpecIndex, Number = number });
                }
            }
        }
    }
}
