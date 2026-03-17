using System.Collections.Generic;
using Serialization;
using UnityEngine;
using Random = UnityEngine.Random;

public class Toolbar : MonoBehaviour
{
    public Player player;
    public RectTransform highlight;
    public UIItemSlot[] slots;
    public int slotIndex;

    private readonly KeyCode[] _keyCodes =
    {
        KeyCode.Alpha1,
        KeyCode.Alpha2,
        KeyCode.Alpha3,
        KeyCode.Alpha4,
        KeyCode.Alpha5,
        KeyCode.Alpha6,
        KeyCode.Alpha7,
        KeyCode.Alpha8,
        KeyCode.Alpha9,
    };

    private void Start()
    {
        byte index = 1;
        foreach (UIItemSlot s in slots)
        {
            ItemStack stack = new ItemStack(index, Random.Range(2, 65));
            ItemSlot slot = new ItemSlot(s, stack);
            index++;
        }
    }

    private void Update()
    {
        // SCROLL WHEEL
        float scroll = Input.GetAxis("Mouse ScrollWheel");

        if (scroll != 0 && !Input.GetKey(KeyCode.LeftAlt))
        {
            if (scroll > 0)
                slotIndex--;
            else
                slotIndex++;

            if (slotIndex > slots.Length - 1)
                slotIndex = 0;

            if (slotIndex < 0)
                slotIndex = slots.Length - 1;

            SetItemSlot();
        }


        // NUMBER KEYS
        for (int i = 0; i < _keyCodes.Length; i++)
        {
            if (Input.GetKeyDown(_keyCodes[i]))
            {
                slotIndex = i;
                SetItemSlot();
            }
        }
    }

    private void SetItemSlot()
    {
        highlight.position = slots[slotIndex].slotIcon.transform.position;
    }

    // --- SAVE / LOAD LOGIC ---

    #region Save / Load Logic

    public List<InventoryItemData> GetInventoryData()
    {
        List<InventoryItemData> list = new List<InventoryItemData>();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].ItemSlot.HasItem)
            {
                list.Add(new InventoryItemData(
                    i,
                    slots[i].ItemSlot.Stack.ID,
                    slots[i].ItemSlot.Stack.Amount
                ));
            }
        }

        return list;
    }

    public void LoadInventoryData(List<InventoryItemData> data)
    {
        // Clear existing
        foreach (UIItemSlot slot in slots) slot.ItemSlot.EmptySlot();

        // Fill from save
        foreach (InventoryItemData item in data)
        {
            // Skip invalid slots
            if (item.slotIndex < 0 || item.slotIndex >= slots.Length) continue;

            // Create stack and insert
            ItemStack stack = new ItemStack(item.itemID, item.amount);
            slots[item.slotIndex].ItemSlot.InsertStack(stack);
        }
    }

    #endregion
}
