using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class DragAndDropHandler : MonoBehaviour
{
    [SerializeField] private UIItemSlot cursorSlot = null;
    private ItemSlot cursorItemSlot;
    private UIItemSlot lastClickedSlot = null;

    [SerializeField] private GraphicRaycaster m_Raycaster = null;
    private PointerEventData m_PointerEventData;
    [SerializeField] private EventSystem m_EventSystem = null;

    private World world;

    private CreativeInventory creativeInventory = null;
    private List<ItemSlot> inventorySlots;

    private void Start()
    {
        world = GameObject.Find("World").GetComponent<World>();

        cursorItemSlot = new ItemSlot(cursorSlot);
    }

    private void Update()
    {
        // UI is closed and cursor slot is empty, do nothing.
        if (!world.inUI && !cursorSlot.HasItem)
            return;

        if (creativeInventory == null)
            creativeInventory = GameObject.Find("CreativeInventory").GetComponent<CreativeInventory>();

        // UI is closed and cursor slot still has item, place item back to original place.
        if (!world.inUI && cursorSlot.HasItem)
        {
            PlaceStackToLastLocation(cursorSlot);
            return;
        }

        cursorSlot.transform.position = Input.mousePosition;

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
        lastClickedSlot = clickedSlot;

        // Both cursor slot & clicked slot are empty, nothing can be done.
        if (!cursorSlot.HasItem && !clickedSlot.HasItem)
            return;

        // In Creative inventory, take stack without removing it from inventory.
        if (clickedSlot.itemSlot.isCreative)
        {
            cursorItemSlot.EmptySlot();
            cursorItemSlot.InsertStack(clickedSlot.itemSlot.stack);
            return;
        }

        // Cursor slot is empty but clicked slot has items, move items to cursor slot.
        if (!cursorSlot.HasItem && clickedSlot.HasItem)
        {
            cursorItemSlot.InsertStack(clickedSlot.itemSlot.TakeAll());
            return;
        }

        // Cursor slot has items but clicked slot is empty, move items to clicked slot.
        if (cursorSlot.HasItem && !clickedSlot.HasItem)
        {
            clickedSlot.itemSlot.InsertStack(cursorItemSlot.TakeAll());
            return;
        }

        // Both cursor slot & clicked slot have items, ...
        if (cursorSlot.HasItem && clickedSlot.HasItem)
        {
            // Both slots contain different items, swap them.
            if (cursorSlot.itemSlot.stack.id != clickedSlot.itemSlot.stack.id)
            {
                SwapStacks(clickedSlot, cursorSlot);
                return;
            }

            // Both slots contain the same item, combine item amount based on stack size
            int maxStackSize = world.blockTypes[cursorSlot.itemSlot.stack.id].stackSize;
            int oldCursorSlotStackAmount = cursorSlot.itemSlot.stack.amount;
            int oldClickedSlotStackAmount = clickedSlot.itemSlot.stack.amount;
            int combinedStackAmount = oldClickedSlotStackAmount + oldCursorSlotStackAmount;

            // Both stack amounts combined greater than max stack size and clicked slot is full, swap them.
            if (combinedStackAmount > maxStackSize && oldClickedSlotStackAmount == maxStackSize)
            {
                SwapStacks(clickedSlot, cursorSlot);
                return;
            }

            // Combine both stacks into one stack on clicked slot, place potentially remaining stack into cursor slot.
            ItemStack remainingStack = CombineStacks(clickedSlot.itemSlot, cursorSlot.itemSlot.stack);
            cursorItemSlot.InsertStack(remainingStack);
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
        lastClickedSlot = clickedSlot;

        // Both cursor slot & clicked slot are empty, nothing can be done.
        if (!cursorSlot.HasItem && !clickedSlot.HasItem)
            return;

        // Cursor slot is empty but clicked slot has items, move items to cursor slot.
        if (!cursorSlot.HasItem && clickedSlot.HasItem)
        {
            cursorItemSlot.InsertStack(clickedSlot.itemSlot.TakeHalve());
            return;
        }

        // Cursor slot has items but clicked slot is empty, move items to clicked slot.
        if (cursorSlot.HasItem && !clickedSlot.HasItem)
        {
            clickedSlot.itemSlot.InsertStack(cursorItemSlot.Take(1));
            return;
        }

        // Both cursor slot & clicked slot have items, ...
        if (cursorSlot.HasItem && clickedSlot.HasItem)
        {
            // Both slots contain different items, do nothing.
            if (cursorSlot.itemSlot.stack.id != clickedSlot.itemSlot.stack.id)
                return;

            // Both slots contain the same item, place one item into clicked slot based on stack size
            int maxStackSize = world.blockTypes[cursorSlot.itemSlot.stack.id].stackSize;

            // Clicked slot is full, do nothing.
            if (clickedSlot.itemSlot.stack.amount + 1 > maxStackSize) return;

            // Clicked slot isn't full, add one item from cursor slot.
            clickedSlot.itemSlot.stack.amount += cursorSlot.itemSlot.Take(1).amount;
            clickedSlot.itemSlot.InsertStack(clickedSlot.itemSlot.stack);
            return;
        }
    }

    private static void SwapStacks(UIItemSlot _clickedSlot, UIItemSlot _cursorSlot)
    {
        ItemStack oldCursorSlot = _cursorSlot.itemSlot.TakeAll();
        ItemStack oldClickedSlot = _clickedSlot.itemSlot.TakeAll();

        _clickedSlot.itemSlot.InsertStack(oldCursorSlot);
        _cursorSlot.itemSlot.InsertStack(oldClickedSlot);
    }

    private ItemStack CombineStacks(ItemSlot _slotA, ItemStack _stack)
    {
        int maxStackSize = world.blockTypes[_stack.id].stackSize;
        int oldSlotAStackAmount = _slotA.stack.amount;
        int combinedStackAmount = oldSlotAStackAmount + _stack.amount;

        // Both stack amounts combined is less than or equals max stack size and slot A isn't full, combine both into one stack.
        if (combinedStackAmount <= maxStackSize)
        {
            _stack.amount = combinedStackAmount;
            _slotA.InsertStack(_stack);
            return null;
        }

        // Both stack amounts combined greater than max stack size,  create new stack of the remaining amount.
        if (combinedStackAmount > maxStackSize && oldSlotAStackAmount != maxStackSize)
        {
            int stackAmountRemaining = combinedStackAmount - maxStackSize;

            // Full Clicked Slot
            _slotA.stack.amount = maxStackSize;
            _slotA.InsertStack(_slotA.stack);

            // Remaining Cursor Slot
            _stack.amount = stackAmountRemaining;
            return _stack;
        }

        return null;
    }

    private void PlaceStackToLastLocation(UIItemSlot _cursorSlot)
    {
        // Last clicked slot is empty, move cursor items to slot.
        if (!lastClickedSlot.HasItem)
        {
            lastClickedSlot.itemSlot.InsertStack(_cursorSlot.itemSlot.TakeAll());
            return;
        }

        if (lastClickedSlot.itemSlot.stack.id != _cursorSlot.itemSlot.stack.id || lastClickedSlot.itemSlot.isCreative)
        {
            // TODO: Better full inventory fallback
            PlaceStackToAvailableInventorySlot(_cursorSlot);
            return;
        }

        // TODO: Create separate CombineStack function, returning new stack if needed.
        // Last clicked slot isn't empty, combine stacks.
        // Combine both stacks into one stack on clicked slot, place potentially remaining stack into cursor slot.
        ItemStack remainingStack = CombineStacks(lastClickedSlot.itemSlot, cursorSlot.itemSlot.stack);
        _cursorSlot.itemSlot.InsertStack(remainingStack);

        if (!_cursorSlot.HasItem)
            return;

        // TODO: Better full inventory fallback
        PlaceStackToAvailableInventorySlot(_cursorSlot);
        return;
    }

    private bool PlaceStackToAvailableInventorySlot(UIItemSlot _uiItemSlot)
    {
        if (!_uiItemSlot.HasItem)
            return true;

        int maxStackSize = world.blockTypes[_uiItemSlot.itemSlot.stack.id].stackSize;

        // First fill slots in inventory with same item.
        foreach (ItemSlot slot in creativeInventory.slots.Where(slot => !slot.isCreative && slot.HasItem && slot.stack.id == _uiItemSlot.itemSlot.stack.id && slot.stack.amount < maxStackSize))
        {
            ItemStack remainingStack = CombineStacks(slot, _uiItemSlot.itemSlot.stack);
            _uiItemSlot.itemSlot.InsertStack(remainingStack);
            if (PlaceStackToAvailableInventorySlot(_uiItemSlot))
                return true;
        }

        // After that, fill remaining empty slots
        foreach (ItemSlot slot in creativeInventory.slots.Where(slot => !slot.isCreative && !slot.HasItem))
        {
            slot.InsertStack(_uiItemSlot.itemSlot.TakeAll());
            return true;
        }

        // Inventory is full
        Debug.Log($"Inventory is full, remaining stack: ID = {_uiItemSlot.itemSlot.stack.id}, amount = {_uiItemSlot.itemSlot.stack.amount}");
        return false;
    }

    private UIItemSlot CheckForSlot()
    {
        m_PointerEventData = new PointerEventData(m_EventSystem);
        m_PointerEventData.position = Input.mousePosition;

        List<RaycastResult> results = new List<RaycastResult>();
        m_Raycaster.Raycast(m_PointerEventData, results);

        foreach (RaycastResult result in results)
        {
            if (result.gameObject.CompareTag("UIItemSlot"))
            {
                return result.gameObject.GetComponent<UIItemSlot>();
            }
        }

        return null;
    }
}