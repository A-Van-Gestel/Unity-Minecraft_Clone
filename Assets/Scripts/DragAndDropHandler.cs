using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Serialization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class DragAndDropHandler : MonoBehaviour
{
    [SerializeField]
    private UIItemSlot _cursorSlot;

    private ItemSlot _cursorItemSlot;
    private UIItemSlot _lastClickedSlot;

    [SerializeField]
    private GraphicRaycaster _m_Raycaster;

    private PointerEventData _m_PointerEventData;

    [SerializeField]
    private EventSystem _m_EventSystem;

    private World _world;

    private CreativeInventory _creativeInventory;

    private void Start()
    {
        _world = GameObject.Find("World").GetComponent<World>();

        _cursorItemSlot = new ItemSlot(_cursorSlot);
    }

    private void Update()
    {
        // UI is closed and cursor slot is empty, do nothing.
        if (!_world.inUI && !_cursorSlot.HasItem)
            return;

        if (_creativeInventory == null)
            _creativeInventory = GameObject.Find("CreativeInventory").GetComponent<CreativeInventory>();

        // UI is closed and cursor slot still has item, place item back to original place.
        if (!_world.inUI && _cursorSlot.HasItem)
        {
            PlaceStackToLastLocation(_cursorSlot);
            return;
        }

        _cursorSlot.transform.position = Input.mousePosition;

        // Left click behavior: Take full stack, place full stack, swap stack if different items
        if (Input.GetMouseButtonDown(0))
            HandleSlotLeftClick(CheckForSlot());

        // Right click behavior: Take halve stack, place one item
        if (Input.GetMouseButtonDown(1))
            HandleSlotRightClick(CheckForSlot());
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
            int maxStackSize = _world.blockTypes[_cursorSlot.ItemSlot.Stack.ID].stackSize;
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
            int maxStackSize = _world.blockTypes[_cursorSlot.ItemSlot.Stack.ID].stackSize;

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
        int maxStackSize = _world.blockTypes[stack.ID].stackSize;
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

        int maxStackSize = _world.blockTypes[uiItemSlot.ItemSlot.Stack.ID].stackSize;

        // First fill slots in inventory with same item.
        foreach (ItemSlot slot in _creativeInventory.Slots.Where(slot => !slot.IsCreative && slot.HasItem && slot.Stack.ID == uiItemSlot.ItemSlot.Stack.ID && slot.Stack.Amount < maxStackSize))
        {
            ItemStack remainingStack = CombineStacks(slot, uiItemSlot.ItemSlot.Stack);
            uiItemSlot.ItemSlot.InsertStack(remainingStack);
            if (PlaceStackToAvailableInventorySlot(uiItemSlot))
                return true;
        }

        // After that, fill remaining empty slots
        foreach (ItemSlot slot in _creativeInventory.Slots.Where(slot => !slot.IsCreative && !slot.HasItem))
        {
            slot.InsertStack(uiItemSlot.ItemSlot.TakeAll());
            return true;
        }

        // Inventory is full
        Debug.Log($"Inventory is full, remaining stack: ID = {uiItemSlot.ItemSlot.Stack.ID}, amount = {uiItemSlot.ItemSlot.Stack.Amount}");
        return false;
    }

    [CanBeNull]
    private UIItemSlot CheckForSlot()
    {
        _m_PointerEventData = new PointerEventData(_m_EventSystem);
        _m_PointerEventData.position = Input.mousePosition;

        List<RaycastResult> results = new List<RaycastResult>();
        _m_Raycaster.Raycast(_m_PointerEventData, results);

        foreach (RaycastResult result in results)
        {
            if (result.gameObject.CompareTag("UIItemSlot"))
            {
                return result.gameObject.GetComponent<UIItemSlot>();
            }
        }

        return null;
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
