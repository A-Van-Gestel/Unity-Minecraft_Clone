using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UIItemSlot : MonoBehaviour
{
    public bool isLinked;
    public ItemSlot ItemSlot;
    public Image slotImage;
    public Image slotIcon;
    public TextMeshProUGUI slotAmount;

    private World _world;

    private void Awake()
    {
        _world = GameObject.Find("World").GetComponent<World>();
    }

    public bool HasItem => ItemSlot != null && ItemSlot.HasItem;

    public void Link(ItemSlot itemSlot)
    {
        ItemSlot = itemSlot;
        isLinked = true;
        ItemSlot.LinkUISlot(this);
        UpdateSlot();
    }

    public void Unlink()
    {
        ItemSlot.UnlinkUISlot();
        ItemSlot = null;
        UpdateSlot();
    }

    public void UpdateSlot()
    {
        if (ItemSlot != null && ItemSlot.HasItem)
        {
            slotIcon.sprite = _world.blockTypes[ItemSlot.Stack.ID].icon;
            slotAmount.text = ItemSlot.Stack.Amount.ToString();
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
            ItemSlot.UnlinkUISlot();
        }
    }
}

public class ItemSlot
{
    public ItemStack Stack;
    private UIItemSlot _uiItemSlot;
    public bool IsCreative;

    public ItemSlot(UIItemSlot uiItemSlot)
    {
        Stack = null;
        _uiItemSlot = uiItemSlot;
        _uiItemSlot.Link(this);
    }

    public ItemSlot(UIItemSlot uiItemSlot, ItemStack stack)
    {
        Stack = stack;
        _uiItemSlot = uiItemSlot;
        _uiItemSlot.Link(this);
    }

    public void LinkUISlot(UIItemSlot uiSlot)
    {
        _uiItemSlot = uiSlot;
    }

    public void UnlinkUISlot()
    {
        _uiItemSlot = null;
    }

    public void EmptySlot()
    {
        Stack = null;
        if (_uiItemSlot != null)
        {
            _uiItemSlot.UpdateSlot();
        }
    }

    public ItemStack Take(int amount)
    {
        // Asked more than amount available, return amount available and empty slot. Or asked exactly amount available, return asked amount and empty slot.
        if (amount >= Stack.Amount)
            return _uiItemSlot.ItemSlot.TakeAll();

        // Asked less than amount available, return asked amount and reduce stack amount.
        ItemStack handOver = new ItemStack(Stack.ID, amount);

        // Don't update slot info when slot is creative
        if (IsCreative) return handOver;

        Stack.Amount -= amount;
        _uiItemSlot.UpdateSlot();

        return handOver;
    }

    public ItemStack TakeAll()
    {
        ItemStack handOver = new ItemStack(Stack.ID, Stack.Amount);

        // Don't update slot info when slot is creative
        if (IsCreative) return handOver;

        EmptySlot();
        return handOver;
    }

    public ItemStack TakeHalve()
    {
        int halveAmount = Mathf.CeilToInt(Stack.Amount / 2.0f);
        ItemStack halveStack = new ItemStack(Stack.ID, halveAmount);

        // Don't update slot info when slot is creative
        if (IsCreative) return halveStack;


        Stack.Amount -= halveAmount;

        // If remaining stack amount is 0, remove stack from slot. Else update slot with new amount.
        if (Stack.Amount == 0)
            EmptySlot();
        else
            _uiItemSlot.UpdateSlot();

        return halveStack;
    }

    public void InsertStack(ItemStack stack)
    {
        if (stack == null)
        {
            EmptySlot();
            return;
        }

        Stack = stack;
        _uiItemSlot.UpdateSlot();
    }

    public bool HasItem
    {
        get
        {
            if (Stack != null)
                return true;
            return false;
        }
    }
}
