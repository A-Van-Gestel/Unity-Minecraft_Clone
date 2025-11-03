public class ItemStack
{
    public readonly byte ID;
    public int Amount;

    public ItemStack(byte id, int amount)
    {
        ID = id;
        Amount = amount;
    }
}
