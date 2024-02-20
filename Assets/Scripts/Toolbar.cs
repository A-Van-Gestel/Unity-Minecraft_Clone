using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class Toolbar : MonoBehaviour
{
    public Player player;
    public RectTransform highlight;
    public UIItemSlot[] slots;
    public int slotIndex = 0;

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
        highlight.position = slots[slotIndex].slotIcon.transform.position;
    }
}
