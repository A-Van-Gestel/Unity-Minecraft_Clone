using System.Collections.Generic;
using UI.Builders;
using UnityEngine;
using UnityEngine.UI;

namespace UI.Toast
{
    /// <summary>
    /// The toast surface: a corner-anchored, non-overlapping stack of transient cards, each owning its own
    /// dismissal timer. Any system can raise one through <see cref="Show(in ToastRequest)"/> without knowing
    /// anything about UI construction, layout or timing.
    /// </summary>
    /// <remarks>
    /// Hosts its own overlay canvas at <see cref="SORT_ORDER"/> — above the benchmark results modal at 200 —
    /// and is spawned as a runtime GameObject by <see cref="WorldUIManager"/>, exactly as the console is. No
    /// scene object, no prefab, no serialized reference to break.
    /// <para>
    /// Stacking is delegated to a <see cref="VerticalLayoutGroup"/> per anchor rather than hand-rolled:
    /// non-overlap, variable card heights from wrapped titles, and mid-stack gap closure all fall out of the
    /// normal layout rebuild, and hand-rolled offset math would have to re-derive all three.
    /// </para>
    /// </remarks>
    public class ToastManager : MonoBehaviour
    {
        #region Layout constants

        /// <summary>Canvas sorting order. Above the benchmark results modal (200) — toasts are always visible.</summary>
        private const int SORT_ORDER = 250;

        /// <summary>Distance from the screen edges to the nearest card, in canvas reference pixels.</summary>
        private const float EDGE_MARGIN = 16f;

        /// <summary>Vertical gap between stacked cards.</summary>
        private const float CARD_SPACING = 8f;

        /// <summary>Seconds a card dwells when the request does not ask for a duration of its own.</summary>
        private const float DEFAULT_DWELL_SECONDS = 4.5f;

        /// <summary>How many cards one anchor shows at once. Further requests wait for a slot.</summary>
        private const int MAX_CARDS_PER_ANCHOR = 3;

        /// <summary>
        /// How many requests one anchor holds waiting for a slot. Beyond this the newest is dropped.
        /// </summary>
        /// <remarks>
        /// Bounded because a toast is a transient notice: a system raising them faster than they can be read
        /// would otherwise build a backlog that outlives whatever caused it, showing minutes-stale cards.
        /// The newest is dropped rather than the oldest so the queue stays in the order it was raised.
        /// </remarks>
        private const int MAX_QUEUED_PER_ANCHOR = 8;

        /// <summary>
        /// How many requests one anchor can accept at once: the cards on screen plus the queue behind them.
        /// Anything beyond this is dropped.
        /// </summary>
        /// <remarks>
        /// Public so a caller that raises a batch can size it to what will actually be shown, rather than
        /// hard-coding a number that silently desyncs when either private limit moves.
        /// </remarks>
        public const int AnchorCapacity = MAX_CARDS_PER_ANCHOR + MAX_QUEUED_PER_ANCHOR;

        /// <summary>Number of real anchors, excluding <see cref="ToastAnchor.None"/>.</summary>
        private const int ANCHOR_COUNT = 4;

        #endregion

        /// <summary>The live manager, or null when no scene hosts one.</summary>
        public static ToastManager Instance { get; private set; }

        /// <summary>
        /// Clears the singleton back-reference on play-mode entry. Required because this project runs with
        /// Reload Domain disabled, so a stale reference would otherwise leak into the next play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset() => Instance = null;

        [Tooltip("Corner used by any request that does not name one of its own.")]
        [SerializeField]
        private ToastAnchor _defaultAnchor = ToastAnchor.TopRight;

        /// <summary>One stack per corner, indexed by <see cref="AnchorIndex"/>.</summary>
        private AnchorStack[] _stacks;

        /// <summary>Cards that have finished and can be re-shown, shared across every anchor.</summary>
        private readonly Stack<ToastCard> _free = new Stack<ToastCard>();

        /// <summary>One corner: its container, the cards live in it, and the requests waiting for a slot.</summary>
        private sealed class AnchorStack
        {
            /// <summary>The layout container every card at this corner is parented under.</summary>
            public RectTransform Container;

            /// <summary>Cards currently on screen at this corner, oldest first.</summary>
            public readonly List<ToastCard> Live = new List<ToastCard>();

            /// <summary>Requests waiting for a slot, in the order they were raised.</summary>
            public readonly Queue<ToastRequest> Waiting = new Queue<ToastRequest>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // The canvas first: UIScaleController requires a CanvasScaler and reads it in its own Awake,
            // which runs synchronously inside AddComponent — so the scaler has to exist by then.
            RuntimeUIFactory.ConfigureCanvas(gameObject, SORT_ORDER);
            gameObject.AddComponent<UIScaleController>();

            BuildAnchorContainers();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Raises a toast. Safe to call when no manager exists — toasts are a feedback layer and must never
        /// be able to break the caller.
        /// </summary>
        /// <param name="request">What to show, and where.</param>
        public static void Show(in ToastRequest request)
        {
            if (Instance == null || !request.IsShowable) return;
            Instance.ShowInternal(in request);
        }

        /// <summary>Shows a card at the request anchor, or queues the request when that anchor is full.</summary>
        /// <param name="request">What to show, and where.</param>
        private void ShowInternal(in ToastRequest request)
        {
            ToastAnchor anchor = request.Anchor != ToastAnchor.None ? request.Anchor : _defaultAnchor;
            AnchorStack stack = _stacks[AnchorIndex(anchor)];

            if (stack.Live.Count >= MAX_CARDS_PER_ANCHOR)
            {
                if (stack.Waiting.Count < MAX_QUEUED_PER_ANCHOR) stack.Waiting.Enqueue(request);
                return;
            }

            Spawn(stack, in request);
        }

        /// <summary>Takes a card from the free-list (or builds one) and starts it in this stack.</summary>
        /// <param name="stack">The corner to show at.</param>
        /// <param name="request">What to show.</param>
        private void Spawn(AnchorStack stack, in ToastRequest request)
        {
            ToastCard card = _free.Count > 0 ? _free.Pop() : ToastCard.Create(stack.Container);
            card.transform.SetParent(stack.Container, false);

            stack.Live.Add(card);

            float dwell = request.DwellSeconds > 0f ? request.DwellSeconds : DEFAULT_DWELL_SECONDS;
            card.Show(in request, dwell, OnCardFinished);
        }

        /// <summary>
        /// Returns a finished card to the free-list and admits the next queued request at that anchor.
        /// </summary>
        /// <param name="card">The card that finished its exit.</param>
        /// <remarks>
        /// The card is searched for rather than told which stack it belongs to, because a card is reparented
        /// on reuse and a stack recorded at show time could name the corner it was last shown at instead.
        /// Four stacks of at most three cards makes the scan free.
        /// </remarks>
        private void OnCardFinished(ToastCard card)
        {
            foreach (AnchorStack stack in _stacks)
            {
                if (!stack.Live.Remove(card)) continue;

                // Admitted before this card is pooled, so the queued request never draws the very card it
                // is replacing: re-showing an instance from inside its own Retire() works only while the
                // lifetime coroutine happens to clear its handle first, and that ordering is not something
                // a future edit should have to know about.
                if (stack.Waiting.Count > 0)
                {
                    ToastRequest next = stack.Waiting.Dequeue();
                    Spawn(stack, in next);
                }

                _free.Push(card);
                return;
            }

            // Not in any stack: the card was already released. Still poolable, but nothing to admit.
            _free.Push(card);
        }

        /// <summary>Builds the four corner containers, each a vertical layout that sizes itself to its cards.</summary>
        private void BuildAnchorContainers()
        {
            _stacks = new AnchorStack[ANCHOR_COUNT];

            _stacks[AnchorIndex(ToastAnchor.TopRight)] = CreateStack(ToastAnchor.TopRight, new Vector2(1f, 1f));
            _stacks[AnchorIndex(ToastAnchor.TopLeft)] = CreateStack(ToastAnchor.TopLeft, new Vector2(0f, 1f));
            _stacks[AnchorIndex(ToastAnchor.BottomRight)] =
                CreateStack(ToastAnchor.BottomRight, new Vector2(1f, 0f));
            _stacks[AnchorIndex(ToastAnchor.BottomLeft)] =
                CreateStack(ToastAnchor.BottomLeft, new Vector2(0f, 0f));
        }

        /// <summary>Creates one corner container.</summary>
        /// <param name="anchor">Which corner this stack occupies.</param>
        /// <param name="corner">The corner as anchor/pivot coordinates: x 0 is left, y 0 is bottom.</param>
        /// <returns>The created stack.</returns>
        private AnchorStack CreateStack(ToastAnchor anchor, Vector2 corner)
        {
            GameObject obj = new GameObject($"Stack_{anchor}", typeof(RectTransform));
            obj.transform.SetParent(transform, false);

            RectTransform rect = (RectTransform)obj.transform;
            rect.anchorMin = corner;
            rect.anchorMax = corner;
            rect.pivot = corner;

            // Inset from whichever edges this corner touches: x pushes left at the right edge, y pushes down
            // at the top edge.
            rect.anchoredPosition = new Vector2(
                corner.x > 0.5f ? -EDGE_MARGIN : EDGE_MARGIN,
                corner.y > 0.5f ? -EDGE_MARGIN : EDGE_MARGIN);

            VerticalLayoutGroup layout = obj.AddComponent<VerticalLayoutGroup>();
            layout.spacing = CARD_SPACING;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = corner.x > 0.5f ? TextAnchor.UpperRight : TextAnchor.UpperLeft;

            // Bottom corners grow upward, so the newest card — always the last sibling — has to be drawn at
            // the bottom of the container rather than the top.
            layout.reverseArrangement = corner.y < 0.5f;

            ContentSizeFitter fitter = obj.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return new AnchorStack { Container = rect };
        }

        /// <summary>Maps an anchor to its slot in <see cref="_stacks"/>.</summary>
        /// <param name="anchor">The anchor to index. <see cref="ToastAnchor.None"/> maps to the first slot.</param>
        /// <returns>A zero-based index into the stack array.</returns>
        private static int AnchorIndex(ToastAnchor anchor) => Mathf.Max(0, (int)anchor - 1);
    }
}
