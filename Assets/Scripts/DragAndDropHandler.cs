using System;
using System.Collections;
using System.Collections.Generic;
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
        lastClickedSlot = clickedSlot;
        
        if (clickedSlot == null)
            return;

        // Both cursor slot & clicked slot are empty, nothing can be done.
        if (!cursorSlot.HasItem && !clickedSlot.HasItem)
            return;

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

            // Both stack amounts combined is less than or equals max stack size and clicked slot isn't full, combine both into one stack.
            if (combinedStackAmount <= maxStackSize)
            {
                ItemStack oldCursorSlot = cursorSlot.itemSlot.TakeAll();
                oldCursorSlot.amount = combinedStackAmount;
                clickedSlot.itemSlot.InsertStack(oldCursorSlot);
                return;
            }

            // Both stack amounts combined greater than max stack size,  create new stack of the remaining amount.
            if (combinedStackAmount > maxStackSize && oldClickedSlotStackAmount != maxStackSize)
            {
                int stackAmountRemaining = combinedStackAmount - maxStackSize;

                // Full Clicked Slot
                clickedSlot.itemSlot.stack.amount = maxStackSize;
                clickedSlot.itemSlot.InsertStack(clickedSlot.itemSlot.stack);

                // Remaining Cursor Slot
                cursorItemSlot.stack.amount = stackAmountRemaining;
                cursorItemSlot.InsertStack(cursorItemSlot.stack);
                return;
            }

            // Both stack amounts combined greater than max stack size and clicked slot is full, swap them.
            if (combinedStackAmount > maxStackSize && oldClickedSlotStackAmount == maxStackSize)
            {
                SwapStacks(clickedSlot, cursorSlot);
                return;
            }
        }
    }

    /// <summary>
    /// Take halve stack, place one item.
    /// </summary>
    /// <param name="clickedSlot">Target slot that has been clicked.</param>
    private void HandleSlotRightClick(UIItemSlot clickedSlot)
    {
        lastClickedSlot = clickedSlot;
        
        if (clickedSlot == null)
            return;

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

    private void PlaceStackToLastLocation(UIItemSlot _cursorSlot)
    {
        // Last clicked slot is empty, move cursor items to slot.
        if (!lastClickedSlot.HasItem)
        {
            lastClickedSlot.itemSlot.InsertStack(_cursorSlot.itemSlot.TakeAll());
            return;
        }
        
        // Last clicked slot isn't empty, combine stacks.
        lastClickedSlot.itemSlot.stack.amount += _cursorSlot.itemSlot.TakeAll().amount;
        lastClickedSlot.itemSlot.InsertStack(lastClickedSlot.itemSlot.stack);
        return;
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