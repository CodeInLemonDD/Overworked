using System.Collections.Generic;
using FishNet.Object.Synchronizing;
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
    /// <see cref="Customer"/>'s grant is. And his request stays up until *both* sides have had
    /// their turn — <see cref="FinishTeam"/> is what says so — so this is not a race the way a
    /// customer is. The first team to finish does not take the offer away from the second.
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
        /// What he hands over when he gets it.
        /// </summary>
        /// <remarks>
        /// **One entry, and the first is the only one ever used.** The trade is "this paperwork for
        /// that document"; a colleague holding a bundle would be a different feature with a
        /// different interface, and the label over his head has one line to say it in. More than one
        /// is reported rather than silently ignored.
        /// </remarks>
        [Tooltip("What he hands over. The first document named is the only one used.")]
        [SerializeField]
        private RequestCatalogue.RequestEntry[] _gives;

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
        /// Reused while writing the offer.
        /// </summary>
        private readonly List<DocumentRequest> _offerRows = new();

        /// <summary>
        /// Whether he has already put his offer up this round.
        /// </summary>
        /// <remarks>
        /// Server only, and it is the whole of "one trade per colleague per round". It has to be
        /// remembered rather than derived from the board: the request comes down as soon as every
        /// team is out by either route, and a colleague who took that as his cue would write a
        /// fresh offer on the very next frame — an unlimited supply of the same document.
        ///
        /// **Set when the offer goes up, not when it is collected**, so that a trade everybody
        /// failed is over too. A second chance would be a different feature, and the version of it
        /// that falls out of the board alone is the bad one: the ask would silently become
        /// "图片 2" between attempts, because naming the same documents again hands out the next
        /// number rather than the same one.
        /// </remarks>
        private bool _offered;

        /// <summary>
        /// Which wiring complaint has already been made.
        /// </summary>
        private string _reported;

        /// <summary>
        /// He waits for somebody to answer him rather than giving up on his own.
        /// </summary>
        public override bool UsesWaitingClock => false;

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

            EnsureOffer();
        }

        /// <summary>
        /// Server: puts his ask on the board, once the scene has finished starting up.
        /// </summary>
        /// <remarks>
        /// **Retried every frame rather than written in <c>OnStartServer</c>**, for the reason
        /// <see cref="CustomerSpawner"/> runs on a timer: two scene objects have no guaranteed order
        /// between them, so a store that has not spawned yet is an ordinary thing to meet and not a
        /// mistake to report. The wait costs three static reads a frame until it succeeds, and then
        /// the guard at the top of this method makes every later frame cost one comparison.
        /// </remarks>
        private void EnsureOffer()
        {
            if (!IsServerInitialized || _offered || RequestId >= 0)
                return;

            DocumentStore store = DocumentStore.Instance;
            RequestBoard board = RequestBoard.Instance;
            ScoreBoard scores = ScoreBoard.Instance;

            if (store == null || board == null || scores == null)
                return;

            if (_wants == null || _wants.Length == 0)
            {
                ReportOnce("no trade is authored, so he will stand there asking for nothing");
                return;
            }

            int teamCount = Mathf.Max(1, scores.TeamCount);

            _rows.Clear();
            RequestWriter.Name(_wants, store, teamCount, _rows, this);

            if (_rows.Count == 0)
            {
                ReportOnce("the authored trade asks for nothing, so it can never be completed");
                return;
            }

            if (!NameOffer(store, teamCount))
                return;

            int requestId = board.ServerCreate(_rows);

            if (requestId < 0)
            {
                ReportOnce("the request board refused the trade, so there is nothing on offer");
                return;
            }

            ServerAssign(requestId);

            /* Marked here rather than when somebody collects: the offer exists from this moment,
             * and it is the offer that is once-per-round. See _offered. */
            _offered = true;
        }

        /// <summary>
        /// Server: names the document he hands over, for every team.
        /// </summary>
        /// <remarks>
        /// The documents are created here even though nobody can see them — an unlock is an id, and
        /// an id has to belong to something. Which team's copy comes out is decided later, when
        /// somebody actually collects; this is what makes both copies exist to collect.
        /// </remarks>
        private bool NameOffer(DocumentStore store, int teamCount)
        {
            if (_gives == null || _gives.Length == 0)
            {
                ReportOnce("nothing is authored as the thing he hands over, so agreeing to the trade would be work for nothing");
                return false;
            }

            _offerRows.Clear();
            RequestWriter.Name(_gives, store, teamCount, _offerRows, this);

            if (_offerRows.Count == 0)
            {
                ReportOnce("the authored offer names no documents, so there would be nothing to hand over");
                return false;
            }

            if (_offerRows.Count > 1)
            {
                ReportOnce(
                    $"{_offerRows.Count} documents are authored as the offer, and only the first is ever handed over; " +
                    "the rest will never be seen");
            }

            DocumentRequest offer = _offerRows[0];

            _givesSpec.Value = offer.SpecIndex;
            _givesNumber.Value = offer.Number;

            return true;
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
        /// Server: his request stands until every team has had its turn.
        /// </summary>
        /// <remarks>
        /// The caller has already written "this team is out" into the phase list, so the question
        /// is only whether anybody is left. Delivering and running out both land in that state,
        /// which is what lets one comparison answer for both — a team that has traded with him and
        /// a team that has given up are the same thing to him.
        ///
        /// The request really does have to stay on the board in the meantime. An NPC's ask is what
        /// a team is granted against and what its folder is judged against, so taking it down when
        /// the first side finished would leave the second side able to take a job that could no
        /// longer be completed or paid for.
        /// </remarks>
        protected override bool FinishTeam(int team)
        {
            for (int other = 0; other < TeamCount; other++)
            {
                if (PhaseOf(other) != CustomerPhase.Failed)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Server: the round is being cleared, so he has a fresh trade to offer next time.
        /// </summary>
        protected override void OnServerReset()
        {
            _offered = false;

            base.OnServerReset();
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
