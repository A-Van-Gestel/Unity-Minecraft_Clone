using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.UI;

public class UIItemSlot : MonoBehaviour
{
    public bool isLinked = false;
    public ItemSlot itemSlot;
    public Image slotImage;
    public Image slotIcon;
    public TextMeshProUGUI slotAmount;

    private World world;

    private void Awake()
    {
        world = GameObject.Find("World").GetComponent<World>();
    }

    public bool HasItem
    {
        get
        {
            if (itemSlot == null)
                return false;
            else
                return itemSlot.HasItem;
        }
    }

    public void Link(ItemSlot _itemSlot)
    {
        itemSlot = _itemSlot;
        isLinked = true;
        itemSlot.LinkUISlot(this);
        UpdateSlot();
    }

    public void Unlink()
    {
        itemSlot.UnlinkUISlot();
        itemSlot = null;
        UpdateSlot();
    }

    public void UpdateSlot()
    {
        if (itemSlot != null && itemSlot.HasItem)
        {
            slotIcon.sprite = world.blockTypes[itemSlot.stack.id].icon;
            slotAmount.text = itemSlot.stack.amount.ToString();
            slotIcon.enabled = true;
            slotAmount.enabled = true;
        }
        else
        {
            Clear();
        }
    }

    public void Clear()
    {
        slotIcon.sprite = null;
        slotAmount.text = "";
        slotIcon.enabled = false;
        slotAmount.enabled = false;
    }

    private void OnDestroy()
    {
        if (isLinked)
        {
            itemSlot.UnlinkUISlot();
        }
    }
}

public class ItemSlot
{
    public ItemStack stack = null;
    private UIItemSlot uiItemSlot = null;
    public bool isCreative;

    public ItemSlot(UIItemSlot _uiItemSlot)
    {
        stack = null;
        uiItemSlot = _uiItemSlot;
        uiItemSlot.Link(this);
    }

    public ItemSlot(UIItemSlot _uiItemSlot, ItemStack _stack)
    {
        stack = _stack;
        uiItemSlot = _uiItemSlot;
        uiItemSlot.Link(this);
    }

    public void LinkUISlot(UIItemSlot uiSlot)
    {
        uiItemSlot = uiSlot;
    }

    public void UnlinkUISlot()
    {
        uiItemSlot = null;
    }

    public void EmptySlot()
    {
        stack = null;
        if (uiItemSlot != null)
        {
            uiItemSlot.UpdateSlot();
        }
    }

    public ItemStack Take(int amount)
    {
        // Asked more than amount available, return amount available and empty slot. Or asked exactly amount available, return asked amount and empty slot.
        if (amount >= stack.amount)
            return uiItemSlot.itemSlot.TakeAll();
        
        // Asked less than amount available, return asked amount and reduce stack amount.
        ItemStack handOver = new ItemStack(stack.id, amount);

        // Don't update slot info when slot is creative
        if (isCreative) return handOver;
        
        stack.amount -= amount;
        uiItemSlot.UpdateSlot();

        return handOver;
    }

    public ItemStack TakeAll()
    {
        ItemStack handOver = new ItemStack(stack.id, stack.amount);
        
        // Don't update slot info when slot is creative
        if (isCreative) return handOver;
        
        EmptySlot();
        return handOver;
    }

    public ItemStack TakeHalve()
    {
        int halveAmount = Mathf.CeilToInt(stack.amount / 2.0f);
        ItemStack halveStack = new ItemStack(stack.id, halveAmount);

        // Don't update slot info when slot is creative
        if (isCreative) return halveStack;
        
        
        stack.amount -= halveAmount;
        
        // If remaining stack amount is 0, remove stack from slot. Else update slot with new amount.
        if (stack.amount == 0)
            EmptySlot();
        else
            uiItemSlot.UpdateSlot();

        return halveStack;
    }

    public void InsertStack(ItemStack _stack)
    {
        if (_stack == null)
        {
          EmptySlot();
          return;
        }
        
        stack = _stack;
        uiItemSlot.UpdateSlot();
    }

    public bool HasItem
    {
        get
        {
            if (stack != null)
                return true;
            else
                return false;
        }
    }
}
