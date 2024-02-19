using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Toolbar : MonoBehaviour
{
    private World world;
    public Player player;

    public RectTransform highlight;
    public ItemSlot[] itemSlots;

    private int slotIndex = 0;

    private KeyCode[] keyCodes =
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
        world = GameObject.Find("World").GetComponent<World>();

        foreach (ItemSlot slot in itemSlots)
        {
            slot.icon.sprite = world.blockTypes[slot.itemID].icon;
            slot.icon.enabled = true;
        }

        player.selectedBlockIndex = itemSlots[slotIndex].itemID;
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

            if (slotIndex > itemSlots.Length - 1)
                slotIndex = 0;

            if (slotIndex < 0)
                slotIndex = itemSlots.Length - 1;

            SetItemSlot();
        }

        
        // NUMBER KEYS
        for (int i = 0; i < keyCodes.Length; i++)
        {
            if (Input.GetKeyDown(keyCodes[i]))
            {
                slotIndex = i;
                SetItemSlot();
            }
        }
    }

    private void SetItemSlot()
    {
        highlight.position = itemSlots[slotIndex].icon.transform.position;
        player.selectedBlockIndex = itemSlots[slotIndex].itemID;
    }
}


[System.Serializable]
public class ItemSlot
{
    public byte itemID;
    public Image icon;
}