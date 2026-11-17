namespace UpscaleBuddy.FrameGeneration;

public readonly struct GenerationTicket(AsyncGenerator owner, ulong output, int slot)
{
	public readonly AsyncGenerator Owner = owner;
	public readonly ulong Output = output;
	public readonly int Slot = slot;
}
