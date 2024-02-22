using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// TODO: Create Inventory base class to extend from.
public class CreativeInventory : MonoBehaviour
{
    public GameObject slotPrefab;
    public int itemColumnCount = 9;
    public int itemRowCount = 7;

    private World world;

    private List<ItemSlot> slots = new List<ItemSlot>();

    void Start()
    {
        world = GameObject.Find("World").GetComponent<World>();

        // Dynamically set the size of the creative inventory based of the column & row count and prefab size.
        RectTransform canvasRectTransform = gameObject.GetComponent<RectTransform>();
        GridLayoutGroup canvasGridLayoutGroup = gameObject.GetComponent<GridLayoutGroup>();
        Vector2 prefabSize = slotPrefab.gameObject.GetComponent<RectTransform>().sizeDelta;
        canvasRectTransform.sizeDelta = new Vector2(itemColumnCount, itemRowCount) * prefabSize;
        canvasGridLayoutGroup.cellSize = prefabSize;

        // Calculate how many rows the different block types will fill.
        int creativeSlotRows = Mathf.CeilToInt((float)world.blockTypes.Length / itemColumnCount);
        if ((world.blockTypes.Length - 1) % itemColumnCount == 0)
            creativeSlotRows--;

        // Fill in the slots.
        for (int i = 1; i <= itemColumnCount * itemRowCount; i++)
        {
            GameObject newSlot = Instantiate(slotPrefab, transform);
            int currentRow = Mathf.CeilToInt((float)(i) / itemColumnCount);

            // First fill with creative menu with all block types.
            if (i < world.blockTypes.Length)
            {
                ItemStack stack = new ItemStack((byte)i, world.blockTypes[i].stackSize);
                ItemSlot slot = new ItemSlot(newSlot.GetComponent<UIItemSlot>(), stack);
                slot.isCreative = true;
                slots.Add(slot);
            }
            // Create one row spacer between creative blocks and empty slots by disabling created slot.
            else if (currentRow <= creativeSlotRows + 1)
            {
                newSlot.tag = "Untagged";
                newSlot.GetComponent<Image>().enabled = false;
            }
            // Then fill the remaining space with empty slots.
            else
            {
                ItemSlot slot = new ItemSlot(newSlot.GetComponent<UIItemSlot>());
                slots.Add(slot);
            }
        }
    }
}