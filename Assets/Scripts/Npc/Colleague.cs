using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object.Synchronizing;
using Overworked.Interaction;
using Overworked.Documents;
using Overworked.Stations;
using UnityEngine;

namespace Overworked.Npc
{
    /// <summary>
    /// Somebody in the office who has a document and will part with it for the right paperwork.
    /// </summary>
    /// <remarks>
    /// **A colleague is not a customer. What they share is a shape**, and the shape is most of the
    /// work: an NPC who names what he wants, waits, takes a folder, judges it, and goes quiet when
    /// he is done. Everything about that is in <see cref="Customer"/>, and the only place the two
    /// part company is what the delivering team walks away with — a customer pays points, a
    /// colleague hands over a document. That difference is one override; the name of the base is
    /// kept because renaming a MonoBehaviour churns a prefab and a scene object for no behaviour.
    ///
    /// **He is the third way to get a document, and the only one that is another person.** The other
    /// two are the computer's own filing cabinet and the internet; this is the one that costs you
    /// something you already had. He is what closes the loop the customers open — they ask for
    /// things nobody in the office has, and the answer is a trade.
    ///
    /// **What he is holding is found rather than authored.** The documents only a trade can produce
    /// are the ones whose kind has <see cref="DocumentSource.Trade"/> for its source: taking a
    /// customer's job grants everything the request names *except* those, so they sit in the store
    /// locked and unwanted until somebody brings them in. He walks the live requests looking for one
    /// of those and offers it up. That is why "the colleague has Excel 1" is not a field on this
    /// component — it is not a fact about him, it is a fact about what the office is short of this
    /// minute, and it changes as the round does.
    ///
    /// **His offer is authored, not drawn from the tier table.** A customer's escalating asks are a
    /// difficulty curve, and the tier table is where a curve belongs. A trade is content: "give me
    /// an image and an article and I will let you have my spreadsheet" is a specific piece of level
    /// design, and it is authored on the object that offers it rather than being the ninth row of
    /// something that gets harder on a schedule.
    ///
    /// **But he still moves the difficulty cursor, and that is worth knowing.** His ask is written
    /// through <see cref="RequestBoard.ServerCreate"/> like every other request, so it advances
    /// <see cref="RequestBoard.RequestsMade"/> — which is the cursor the customer spawner reads its
    /// tier from. A colleague on the floor therefore makes the customers escalate faster than they
    /// would without him. That is arguably right, since the office getting busier as more of it
    /// engages is the shape the round wants, but it is a coupling between two NPC types that
    /// nothing declares, and the place to revisit it is when somebody has played enough rounds to
    /// say whether the ramp feels wrong with a colleague in the scene.
    ///
    /// **Both teams trade with him, each on their own.** His ask is one request, because it is a
    /// name and both teams have their own copies of what it names; his reward is per-team, resolved
    /// through <see cref="DocumentStore.TryFind"/> at the moment it is handed over, exactly the way
    /// <see cref="Customer"/>'s grant is.
    ///
    /// **The offer comes down as soon as one team has traded, and that is not a race.** A customer
    /// keeps his request up until both sides are out, because the losing side would otherwise be
    /// left holding documents for a job that no longer exists — but this offer is derived rather
    /// than kept, so taking it down costs nothing at all: the other team walks over and asks, and
    /// the same query writes the same trade back.
    ///
    /// Which is why there is no <see cref="Customer.FinishTeam"/> override here, and why adding one
    /// back is a mistake worth recognising. A colleague who waits for every team to have had its
    /// turn **can never finish on his own in a session where a team has no players**: he holds the
    /// offer open for somebody who is not coming, the player who did trade is shown a trade they
    /// have already done, and — because a team that has traded is out for that offer — he can never
    /// be asked for a new one.
    ///
    /// **He has no clock, and his offer lives exactly as long as the need it answers.** It is not
    /// posted in advance and it is not on a timer: it appears when somebody stands in front of him
    /// and asks, it is about whatever the office is short of at that moment — the Excel 3 the job
    /// somebody just took needs, not the Excel 1 that happens to come first — and it goes away the
    /// moment nothing wants it any more. See <see cref="DropStaleOffer"/>; the shape of it is that
    /// the question which puts the offer up and the question which takes it down are the same
    /// question, so the offer and the reason for it cannot drift apart.
    ///
    /// That is also why a job taken and then abandoned costs nothing. The reason to stop caring
    /// about a document is that the job wanting it went away, and charging for that would land on a
    /// player who did nothing wrong.
    ///
    /// **Running out of time costs nothing but the trade.** A customer's penalty exists because a
    /// customer is a place in a queue that a team wasted, and there are other customers waiting.
    /// Nobody is waiting behind a colleague.
    ///
    /// **He does not use the waiting clock.** A customer appears, and one nobody ever takes has to
    /// clear the way for the next; a colleague is placed in the office like a printer, and there is
    /// no queue behind him to make room for. His offer stands until somebody answers it.
    /// </remarks>
    [DisallowMultipleComponent]
    public class Colleague : Customer
    {
        [Header("Trade")]

        /// <summary>
        /// What he wants before he hands anything over.
        /// </summary>
        /// <remarks>
        /// Same shape as a tier's ask, and named the same way: <see cref="RequestWriter"/> turns it
        /// into documents and rows. Numbers are per (kind, team), so "图片 ×1" here means each team
        /// is asked for its own first image.
        /// </remarks>
        [Tooltip("What he wants, as a count of each kind. Same shape as a request tier.")]
        [SerializeField]
        private RequestCatalogue.RequestEntry[] _wants;

        /// <summary>
        /// Kind of the document he hands over. Replicated.
        /// </summary>
        /// <remarks>
        /// A name rather than an id, for the reason a request is a name: the id behind it is
        /// per-team, so there is no single id that could be stored here. This is what
        /// <see cref="PayOut"/> resolves against the delivering team.
        /// </remarks>
        private readonly SyncVar<int> _givesSpec = new(-1);

        /// <summary>
        /// Number of the document he hands over, within its kind and team. Replicated.
        /// </summary>
        private readonly SyncVar<int> _givesNumber = new(-1);

        /// <summary>
        /// Reused while writing the ask.
        /// </summary>
        /// <remarks>
        /// His own, not the base's: <see cref="Customer"/>'s scratch buffer is documented as
        /// non-re-entrant, and a second caller reaching into it is exactly the mistake that
        /// warning is about.
        /// </remarks>
        private readonly List<DocumentRequest> _rows = new();

        /// <summary>
        /// Which wiring complaint has already been made.
        /// </summary>
        private string _reported;

        /// <summary>
        /// He waits for somebody to answer him rather than giving up on his own.
        /// </summary>
        public override bool UsesWaitingClock => false;

        /// <summary>
        /// Nobody is on a clock, because the offer's life is the need's life.
        /// </summary>
        /// <remarks>
        /// A job taken and then abandoned would otherwise be a job charged for — and the charge
        /// would land on a player who did nothing wrong, since the reason to stop caring about this
        /// document is that the job wanting it went away. See <see cref="DropStaleOffer"/>, which is
        /// what ends the offer instead.
        /// </remarks>
        public override bool UsesPatienceClock => false;

        /// <summary>
        /// He writes his own trade rather than taking a customer's request.
        /// </summary>
        /// <remarks>
        /// Without this the customer spawner would find him — it searches for
        /// <see cref="Customer"/>, which includes everything derived from it — count him as an
        /// empty seat, and hand him a request from the tier table, replacing the trade he had just
        /// written for himself.
        /// </remarks>
        public override bool ProvidesOwnRequest => true;

        /// <summary>
        /// What he is holding, for the label over his head.
        /// </summary>
        /// <remarks>
        /// Without this a player standing in front of him can see what he wants and has no way to
        /// find out what for — and a trade whose reward cannot be seen before agreeing to it is not
        /// a trade, it is a chore. Read on every peer from replicated state, like the request it
        /// sits under.
        /// </remarks>
        public override string OfferLabel
        {
            get
            {
                int spec = _givesSpec.Value;
                int number = _givesNumber.Value;

                if (spec < 0 || number < 0)
                    return null;

                DocumentStore store = DocumentStore.Instance;

                string kind = store != null && store.TryGetSpecAt(spec, out DocumentCatalogue.Spec named)
                    ? named.DisplayName
                    : $"种类 {spec}";

                return $"换 {kind} {number}";
            }
        }

        protected override void Update()
        {
            base.Update();

            DropStaleOffer();
        }

        /// <summary>
        /// Server: he says what he wants when he is asked, and not before.
        /// </summary>
        /// <remarks>
        /// **The offer is not posted in advance.** What he is holding is whatever the office is
        /// short of at that moment — an Excel 3 for a job somebody has actually taken, not an
        /// Excel 1 because it happens to be early in the round — and the only way to know what that
        /// is, is for somebody to want something. So the offer is derived on the press, from the
        /// live requests, and stands until the reason for it goes away.
        ///
        /// A press with nothing behind it does nothing at all, and says nothing. That is the same
        /// answer every other refusal in the office gives, and here it is also the truthful one:
        /// there is no trade because there is nothing to trade about.
        /// </remarks>
        protected override void OnServerInteract(PlayerInteraction player, NetworkConnection conn, float heldSeconds)
        {
            if (RequestId < 0)
                EnsureOffer();

            base.OnServerInteract(player, conn, heldSeconds);
        }

        /// <summary>
        /// Server: takes the offer down when nobody wants what it was for any more.
        /// </summary>
        /// <remarks>
        /// **The offer lasts exactly as long as the need does, and nothing else ends it.** A job
        /// that asked for Excel 3 is delivered, or fails, or is abandoned for another one — and in
        /// every case the request that named it leaves the board, so there is nothing for a
        /// colleague holding an Excel 3 to be for. Standing there offering it would be a trade with
        /// no subject, and worse, it would be the thing the next player found when they came looking
        /// for the document their *new* job wants.
        ///
        /// This is why there is no clock on him. A deadline would be a second thing trying to end
        /// the same offer, and whichever fired first would be an accident rather than a decision.
        ///
        /// The question asked is the same one that put the offer up — "is there a trade-only
        /// document on a live request that somebody still has not been given" — so the offer and the
        /// reason for it cannot drift apart.
        /// </remarks>
        private void DropStaleOffer()
        {
            if (!IsServerInitialized || RequestId < 0)
                return;

            DocumentStore store = DocumentStore.Instance;
            RequestBoard board = RequestBoard.Instance;
            DocumentUnlocks unlocks = DocumentUnlocks.Instance;

            if (store == null || board == null || unlocks == null)
                return;

            /* Asked for the answer only; the two fields it writes as a side effect are set to what
             * they already hold, because a live offer is by definition about something that is still
             * wanted. */
            if (FindHeldDocument(store, board, unlocks))
                return;

            GoIdle();
        }

        /// <summary>
        /// Server: writes the trade he is offering, if there is one to offer.
        /// </summary>
        /// <remarks>
        /// **Reached from a press, not from a clock.** Nothing here runs until somebody stands in
        /// front of him and asks, which is the difference between a colleague who reflects what the
        /// office is short of and one who decided what he wanted before anybody needed anything —
        /// see <see cref="OnServerInteract"/>.
        ///
        /// The scene-ordered guards stay even so: this is the first thing that touches the store and
        /// the board on this object's behalf, and a press can arrive in the same frame the scene
        /// finishes loading.
        /// </remarks>
        private void EnsureOffer()
        {
            if (!IsServerInitialized || RequestId >= 0)
                return;

            DocumentStore store = DocumentStore.Instance;
            RequestBoard board = RequestBoard.Instance;
            ScoreBoard scores = ScoreBoard.Instance;
            DocumentUnlocks unlocks = DocumentUnlocks.Instance;

            if (store == null || board == null || scores == null || unlocks == null)
                return;

            if (_wants == null || _wants.Length == 0)
            {
                ReportOnce("no trade is authored, so he will stand there asking for nothing");
                return;
            }

            if (!WantsSomethingObtainable(store))
                return;

            int teamCount = Mathf.Max(1, scores.TeamCount);

            _rows.Clear();
            RequestWriter.Name(_wants, store, teamCount, _rows, this);

            if (_rows.Count == 0)
            {
                ReportOnce("the authored trade asks for nothing, so it can never be completed");
                return;
            }

            /* Nothing to hold means nothing to offer, which is the ordinary state of a round before
             * anybody has been asked for something nobody in the office can produce. Quiet, not a
             * complaint: this is a wait, and it is retried every frame. */
            if (!FindHeldDocument(store, board, unlocks))
                return;

            int requestId = board.ServerCreate(_rows);

            if (requestId < 0)
            {
                ReportOnce("the request board refused the trade, so there is nothing on offer");
                return;
            }

            ServerAssign(requestId);
        }

        /// <summary>
        /// Server: false when the authored ask includes something only a trade can produce.
        /// </summary>
        /// <remarks>
        /// **A colleague who asks for a trade-only kind can never be paid, and nothing says so.**
        /// Taking his job grants what the request names minus exactly the kinds whose source is
        /// <see cref="DocumentSource.Trade"/> — so asking for one of those is asking for the one
        /// thing he is the only source of, and the exchange can never close. It is the same shape
        /// as agreeing to swap a spreadsheet for a spreadsheet: the offer goes up, a team does the
        /// work, and the delivery is refused for a reason nothing on screen explains.
        ///
        /// Reported rather than refused quietly, and checked here rather than in the catalogue,
        /// because it is not a mistake about one kind — it is a mistake about this colleague's ask,
        /// and it is the ask that has to change.
        /// </remarks>
        private bool WantsSomethingObtainable(DocumentStore store)
        {
            for (int i = 0; i < _wants.Length; i++)
            {
                if (!store.TryGetSpecAt(_wants[i].SpecIndex, out DocumentCatalogue.Spec spec))
                    continue;

                if (spec.Source != (int)DocumentSource.Trade)
                    continue;

                ReportOnce(
                    $"his ask includes '{spec.DisplayName}', which is a trade-only kind. Taking his job would not grant " +
                    "it, so the exchange could never be completed. Ask for something the office can obtain by itself");

                return false;
            }

            return true;
        }

        /// <summary>
        /// Server: works out which document he is holding.
        /// </summary>
        /// <remarks>
        /// **He does not make it. He has it.** The document already exists — whoever wants it named
        /// it, and naming a document is what makes one — and it is sitting locked, waiting for
        /// somebody to bring it into the office. This finds which one that is by walking the live
        /// requests for a kind whose source is <see cref="DocumentSource.Trade"/>, which is exactly
        /// the set that <see cref="Customer"/>'s grant skips and therefore exactly the set that
        /// cannot be obtained any other way.
        ///
        /// **Nothing about which document he holds is authored, and that is the point.** "The
        /// colleague has Excel 1" is not a fact about the colleague; it is a fact about what the
        /// office is currently short of. An authored number would be a guess about a request that
        /// has not been written yet, and the guess failing would be silent in the worst way — a
        /// trade finished, and nothing handed over at the end of it.
        ///
        /// A request whose kind is not trade-sourced is skipped, and so is one a team already has:
        /// the first trade is what makes the second unnecessary. Finding nothing is the ordinary
        /// state early in a round, and is answered with silence rather than a complaint.
        /// </remarks>
        private bool FindHeldDocument(DocumentStore store, RequestBoard board, DocumentUnlocks unlocks)
        {
            for (int i = 0; i < board.Count; i++)
            {
                if (!board.TryGet(i, out DocumentRequest row))
                    continue;

                if (!store.TryGetSpecAt(row.SpecIndex, out DocumentCatalogue.Spec spec))
                    continue;

                if (spec.Source != (int)DocumentSource.Trade)
                    continue;

                /* Asked of **every** team, not of team 0. The copies are separate documents, so one
                 * side already having theirs says nothing about the other — and a side that still
                 * needs it is exactly who the next offer is for. Asking only team 0 got this wrong
                 * in the case that matters most: one side trades and the other runs out of time,
                 * and the side that ran out is the one left with no way to try again. */
                bool wanted = false;

                for (int team = 0; team < TeamCount; team++)
                {
                    if (!store.TryFind(row.SpecIndex, row.Number, team, out int id))
                        continue;

                    if (!unlocks.IsUnlocked(id))
                    {
                        wanted = true;
                        break;
                    }
                }

                if (!wanted)
                    continue;

                _givesSpec.Value = row.SpecIndex;
                _givesNumber.Value = row.Number;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Server: lets the delivering team have a copy of what he is holding.
        /// </summary>
        /// <remarks>
        /// The same resolution <see cref="Customer"/>'s grant does, in the same direction and for
        /// the same reason: what is stored is a name, both teams have their own copy of it, and the
        /// id behind it is per-team. That lookup is the whole of "each side trades for itself" —
        /// team 0 walks away with team 0's document and team 1's is untouched.
        /// </remarks>
        protected override void PayOut(int team)
        {
            DocumentStore store = DocumentStore.Instance;
            DocumentUnlocks unlocks = DocumentUnlocks.Instance;

            if (store == null || unlocks == null)
                return;

            int spec = _givesSpec.Value;
            int number = _givesNumber.Value;

            if (spec < 0 || number < 0)
                return;

            if (store.TryFind(spec, number, team, out int id))
                unlocks.ServerUnlock(id);
        }

        /// <summary>
        /// Server: a team ran out of time, and is charged nothing for it.
        /// </summary>
        /// <remarks>
        /// <see cref="Customer"/>'s five points are for wasting a place in a queue that somebody
        /// else was waiting for. Nobody is waiting behind a colleague, and the team has already lost
        /// the document and the trip — charging as well would be charging twice for one mistake.
        /// </remarks>
        protected override void Penalise(int team)
        {
        }

        /// <summary>
        /// Says once that something is wrong.
        /// </summary>
        /// <remarks>
        /// Once per distinct complaint, matching every other NPC in the project. This runs every
        /// frame until it succeeds, so a scene with an unauthored trade would otherwise fill the
        /// console with the same line at sixty a second.
        /// </remarks>
        private void ReportOnce(string reason)
        {
            if (_reported == reason)
                return;

            _reported = reason;

            Debug.LogError($"{nameof(Colleague)} on {gameObject.name}: {reason}.", this);
        }
    }
}
