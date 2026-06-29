using JetBrains.Annotations;
using Serialization;
using UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Handles moving item stacks between inventory slots via a floating cursor slot.
/// Slot interactions are driven by EventSystem pointer events forwarded from
/// <see cref="UIItemSlot"/> (mouse and touch alike); this class only decides
/// what a left-click / right-click / tap / long-press means.
/// </summary>
public class DragAndDropHandler : MonoBehaviour
{
    /// <summary>Singleton instance, set in <see cref="Awake"/>. Used by <see cref="UIItemSlot"/> to forward pointer events.</summary>
    public static DragAndDropHandler Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void DomainReset()
    {
        Instance = null;
    }

    [SerializeField]
    private UIItemSlot _cursorSlot;

    private ItemSlot _cursorItemSlot;
    private UIItemSlot _lastClickedSlot;

    private World _world;
    private InputManager _input;

    private CreativeInventory _creativeInventory;

    // --- Long-press state for mobile right-click emulation ---
    // A press is in flight while _pendingSlot is non-null.
    private const float LONG_PRESS_THRESHOLD = 0.4f;
    private bool _isMobile;
    private float _touchDownTime;
    private bool _longPressHandled;
    private UIItemSlot _pendingSlot;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        _world = GameObject.Find("World").GetComponent<World>();
        _input = InputManager.Instance;
        _isMobile = Application.isMobilePlatform;

        _cursorItemSlot = new ItemSlot(_cursorSlot);

        // The cursor slot follows the pointer; its graphics must never block
        // raycasts or it would swallow every click meant for the slot below it.
        foreach (Graphic graphic in _cursorSlot.GetComponentsInChildren<Graphic>(true))
            graphic.raycastTarget = false;
    }

    private void Update()
    {
        bool inventoryOpen = IsInventoryOpen;

        // Inventory closed mid-press: drop any pending tap / long-press so the
        // stale slot can't receive a ghost click when the inventory reopens.
        if (!inventoryOpen)
            CancelPendingPress();

        // Inventory is closed and cursor slot is empty, do nothing.
        if (!inventoryOpen && !_cursorSlot.HasItem)
            return;

        if (_creativeInventory == null)
            _creativeInventory = GameObject.Find("CreativeInventory").GetComponent<CreativeInventory>();

        // Inventory is closed and cursor slot still has item, place item back to original place.
        if (!inventoryOpen && _cursorSlot.HasItem)
        {
            PlaceStackToLastLocation(_cursorSlot);
            return;
        }

        _cursorSlot.transform.position = _input.MousePosition;

        // Mobile long-press fires while the finger is still down, so it must be
        // polled here rather than waiting for the pointer-up event.
        if (_isMobile && _pendingSlot != null && !_longPressHandled
            && Time.unscaledTime - _touchDownTime >= LONG_PRESS_THRESHOLD)
        {
            HandleSlotRightClick(_pendingSlot);
            _longPressHandled = true;
        }
    }

    /// <summary><c>true</c> while the creative inventory UI is open.</summary>
    private static bool IsInventoryOpen => WorldUIManager.Instance != null && WorldUIManager.Instance.IsCreativeInventoryOpen;

    // --- POINTER EVENT ENTRY POINTS (forwarded by UIItemSlot) ---

    /// <summary>
    /// Starts tap / long-press detection on mobile. The target slot is captured
    /// at press time so the action doesn't depend on pointer position at release.
    /// No-op on desktop, which uses <see cref="OnSlotPointerClick"/> instead.
    /// </summary>
    /// <param name="slot">The slot the pointer was pressed on.</param>
    /// <param name="eventData">The pointer event data from the EventSystem.</param>
    public void OnSlotPointerDown(UIItemSlot slot, PointerEventData eventData)
    {
        if (!_isMobile || !IsInventoryOpen) return;

        _touchDownTime = Time.unscaledTime;
        _longPressHandled = false;
        _pendingSlot = slot;
    }

    /// <summary>
    /// Completes a mobile tap: a short tap acts as left-click (full stack); if the
    /// long-press (right-click) already fired in <see cref="Update"/>, the release does nothing.
    /// No-op on desktop, which uses <see cref="OnSlotPointerClick"/> instead.
    /// </summary>
    /// <param name="slot">The slot the pointer was released over (unused; the press-time slot is authoritative).</param>
    /// <param name="eventData">The pointer event data from the EventSystem.</param>
    public void OnSlotPointerUp(UIItemSlot slot, PointerEventData eventData)
    {
        if (!_isMobile || _pendingSlot == null) return;

        // Inventory closed between press and release, drop the gesture.
        if (!IsInventoryOpen)
        {
            CancelPendingPress();
            return;
        }

        if (!_longPressHandled)
            HandleSlotLeftClick(_pendingSlot);

        _pendingSlot = null;
    }

    /// <summary>
    /// Drops any in-flight tap / long-press gesture without performing a click.
    /// </summary>
    private void CancelPendingPress()
    {
        _pendingSlot = null;
    }

    /// <summary>
    /// Desktop click handling: left click takes / places / swaps the full stack,
    /// right click takes half a stack / places one item.
    /// No-op on mobile, where taps are handled via pointer down / up.
    /// </summary>
    /// <param name="slot">The slot that was clicked.</param>
    /// <param name="eventData">The pointer event data from the EventSystem.</param>
    public void OnSlotPointerClick(UIItemSlot slot, PointerEventData eventData)
    {
        if (_isMobile || !IsInventoryOpen) return;

        switch (eventData.button)
        {
            case PointerEventData.InputButton.Left:
                HandleSlotLeftClick(slot);
                break;
            case PointerEventData.InputButton.Right:
                HandleSlotRightClick(slot);
                break;
        }
    }

    /// <summary>
    /// Take full stack, place full stack, swap stack if different items.
    /// </summary>
    /// <param name="clickedSlot">Target slot that has been clicked.</param>
    private void HandleSlotLeftClick(UIItemSlot clickedSlot)
    {
        if (clickedSlot == null)
            return;

        // Save last clicked slot for later use to reset slot back to last known position.
        _lastClickedSlot = clickedSlot;

        // Both cursor & clicked slots are empty, nothing can be done.
        if (!_cursorSlot.HasItem && !clickedSlot.HasItem)
            return;

        // In Creative inventory, take stack without removing it from inventory.
        if (clickedSlot.ItemSlot.IsCreative)
        {
            _cursorItemSlot.EmptySlot();
            _cursorItemSlot.InsertStack(clickedSlot.ItemSlot.Stack);
            return;
        }

        // Cursor slot is empty but clicked slot has items, move items to cursor slot.
        if (!_cursorSlot.HasItem && clickedSlot.HasItem)
        {
            _cursorItemSlot.InsertStack(clickedSlot.ItemSlot.TakeAll());
            return;
        }

        // Cursor slot has items but clicked slot is empty, move items to clicked slot.
        if (_cursorSlot.HasItem && !clickedSlot.HasItem)
        {
            clickedSlot.ItemSlot.InsertStack(_cursorItemSlot.TakeAll());
            return;
        }

        // Both cursor & clicked slots have items, ...
        if (_cursorSlot.HasItem && clickedSlot.HasItem)
        {
            // Both slots contain different items, swap them.
            if (_cursorSlot.ItemSlot.Stack.ID != clickedSlot.ItemSlot.Stack.ID)
            {
                SwapStacks(clickedSlot, _cursorSlot);
                return;
            }

            // Both slots contain the same item, combine item amount based on stack size
            int maxStackSize = _world.BlockTypes[_cursorSlot.ItemSlot.Stack.ID].stackSize;
            int oldCursorSlotStackAmount = _cursorSlot.ItemSlot.Stack.Amount;
            int oldClickedSlotStackAmount = clickedSlot.ItemSlot.Stack.Amount;
            int combinedStackAmount = oldClickedSlotStackAmount + oldCursorSlotStackAmount;

            // Both stack amounts combined greater than max stack size and clicked slot is full, swap them.
            if (combinedStackAmount > maxStackSize && oldClickedSlotStackAmount == maxStackSize)
            {
                SwapStacks(clickedSlot, _cursorSlot);
                return;
            }

            // Combine both stacks into one stack on clicked slot, place potentially remaining stack into cursor slot.
            ItemStack remainingStack = CombineStacks(clickedSlot.ItemSlot, _cursorSlot.ItemSlot.Stack);
            _cursorItemSlot.InsertStack(remainingStack);
        }
    }

    /// <summary>
    /// Take halve stack, place one item.
    /// </summary>
    /// <param name="clickedSlot">Target slot that has been clicked.</param>
    private void HandleSlotRightClick(UIItemSlot clickedSlot)
    {
        if (clickedSlot == null)
            return;

        // Save last clicked slot for later use to reset slot back to last known position.
        _lastClickedSlot = clickedSlot;

        // Both cursor & clicked slots are empty, nothing can be done.
        if (!_cursorSlot.HasItem && !clickedSlot.HasItem)
            return;

        // Cursor slot is empty but clicked slot has items, move items to cursor slot.
        if (!_cursorSlot.HasItem && clickedSlot.HasItem)
        {
            _cursorItemSlot.InsertStack(clickedSlot.ItemSlot.TakeHalve());
            return;
        }

        // Cursor slot has items but clicked slot is empty, move items to clicked slot.
        if (_cursorSlot.HasItem && !clickedSlot.HasItem)
        {
            clickedSlot.ItemSlot.InsertStack(_cursorItemSlot.Take(1));
            return;
        }

        // Both cursor & clicked slots have items, ...
        if (_cursorSlot.HasItem && clickedSlot.HasItem)
        {
            // Both slots contain different items, do nothing.
            if (_cursorSlot.ItemSlot.Stack.ID != clickedSlot.ItemSlot.Stack.ID)
                return;

            // Both slots contain the same item, place one item into clicked slot based on stack size
            int maxStackSize = _world.BlockTypes[_cursorSlot.ItemSlot.Stack.ID].stackSize;

            // Clicked slot is full, do nothing.
            if (clickedSlot.ItemSlot.Stack.Amount + 1 > maxStackSize) return;

            // Clicked slot isn't full, add one item from cursor slot.
            clickedSlot.ItemSlot.Stack.Amount += _cursorSlot.ItemSlot.Take(1).Amount;
            clickedSlot.ItemSlot.InsertStack(clickedSlot.ItemSlot.Stack);
        }
    }

    private static void SwapStacks(UIItemSlot clickedSlot, UIItemSlot cursorSlot)
    {
        ItemStack oldCursorSlot = cursorSlot.ItemSlot.TakeAll();
        ItemStack oldClickedSlot = clickedSlot.ItemSlot.TakeAll();

        clickedSlot.ItemSlot.InsertStack(oldCursorSlot);
        cursorSlot.ItemSlot.InsertStack(oldClickedSlot);
    }

    [CanBeNull]
    private ItemStack CombineStacks(ItemSlot slotA, ItemStack stack)
    {
        int maxStackSize = _world.BlockTypes[stack.ID].stackSize;
        int oldSlotAStackAmount = slotA.Stack.Amount;
        int combinedStackAmount = oldSlotAStackAmount + stack.Amount;

        // Both stack amounts combined is less than or equals max stack size and slot A isn't full, combine both into one stack.
        if (combinedStackAmount <= maxStackSize)
        {
            stack.Amount = combinedStackAmount;
            slotA.InsertStack(stack);
            return null;
        }

        // Both stack amounts combined greater than max stack size,  create new stack of the remaining amount.
        if (combinedStackAmount > maxStackSize && oldSlotAStackAmount != maxStackSize)
        {
            int stackAmountRemaining = combinedStackAmount - maxStackSize;

            // Full Clicked Slot
            slotA.Stack.Amount = maxStackSize;
            slotA.InsertStack(slotA.Stack);

            // Remaining Cursor Slot
            stack.Amount = stackAmountRemaining;
            return stack;
        }

        return null;
    }

    private void PlaceStackToLastLocation(UIItemSlot cursorSlot)
    {
        // Last clicked slot is empty, move cursor items to slot.
        if (!_lastClickedSlot.HasItem)
        {
            _lastClickedSlot.ItemSlot.InsertStack(cursorSlot.ItemSlot.TakeAll());
            return;
        }

        if (_lastClickedSlot.ItemSlot.Stack.ID != cursorSlot.ItemSlot.Stack.ID || _lastClickedSlot.ItemSlot.IsCreative)
        {
            // TODO: Better full inventory fallback
            PlaceStackToAvailableInventorySlot(cursorSlot);
            return;
        }

        // TODO: Create separate CombineStack function, returning new stack if needed.
        // Last clicked slot isn't empty, combine stacks.
        // Combine both stacks into one stack on clicked slot, place potentially remaining stack into cursor slot.
        ItemStack remainingStack = CombineStacks(_lastClickedSlot.ItemSlot, _cursorSlot.ItemSlot.Stack);
        cursorSlot.ItemSlot.InsertStack(remainingStack);

        if (!cursorSlot.HasItem)
            return;

        // TODO: Better full inventory fallback
        PlaceStackToAvailableInventorySlot(cursorSlot);
    }

    private bool PlaceStackToAvailableInventorySlot(UIItemSlot uiItemSlot)
    {
        if (!uiItemSlot.HasItem)
            return true;

        int maxStackSize = _world.BlockTypes[uiItemSlot.ItemSlot.Stack.ID].stackSize;

        // First fill slots in inventory with same item.
        foreach (ItemSlot slot in _creativeInventory.Slots)
        {
            if (slot.IsCreative || !slot.HasItem || slot.Stack.ID != uiItemSlot.ItemSlot.Stack.ID || slot.Stack.Amount >= maxStackSize) continue;

            ItemStack remainingStack = CombineStacks(slot, uiItemSlot.ItemSlot.Stack);
            uiItemSlot.ItemSlot.InsertStack(remainingStack);
            if (PlaceStackToAvailableInventorySlot(uiItemSlot))
                return true;
        }

        // After that, fill remaining empty slots
        foreach (ItemSlot slot in _creativeInventory.Slots)
        {
            if (slot.IsCreative || slot.HasItem) continue;

            slot.InsertStack(uiItemSlot.ItemSlot.TakeAll());
            return true;
        }

        // Inventory is full
        Debug.Log($"Inventory is full, remaining stack: ID = {uiItemSlot.ItemSlot.Stack.ID.ToString()}, amount = {uiItemSlot.ItemSlot.Stack.Amount.ToString()}");
        return false;
    }

    // --- SAVE / LOAD LOGIC ---

    #region Save / Load Logic

    public CursorItemData GetCursorData()
    {
        if (_cursorSlot != null && _cursorSlot.HasItem)
        {
            return new CursorItemData
            {
                itemID = _cursorSlot.ItemSlot.Stack.ID,
                amount = _cursorSlot.ItemSlot.Stack.Amount,
                originSlotIndex = -1, // We don't track origin currently, could add later
            };
        }

        return null;
    }

    public void LoadCursorData(CursorItemData data)
    {
        if (data != null && data.itemID != 0)
        {
            ItemStack stack = new ItemStack(data.itemID, data.amount);
            _cursorSlot.ItemSlot.InsertStack(stack);
        }
        else
        {
            _cursorSlot.ItemSlot.EmptySlot();
        }
    }

    #endregion
}
