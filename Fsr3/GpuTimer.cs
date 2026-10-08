using System;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.D3D11_QUERY;
using static TerraFX.Interop.Windows.Windows;

namespace UpscaleBuddy.Fsr3;

/// <summary>
/// D3D11 timestamps at a few marks per frame, read back frames later without stalling
/// </summary>
public unsafe class GpuTimer: IDisposable
{
	private const int Slots = 4;

	// Per slot: disjoint, then one timestamp per mark
	private readonly ID3D11Query*[,] queries;
	private readonly bool[] pending = new bool[Slots];
	private int slot;

	public GpuTimer(ID3D11Device* device, int marks)
	{
		this.queries = new ID3D11Query*[Slots, marks + 1];
		this.Ms = new double[marks - 1];
		try
		{
			for (int s = 0; s < Slots; s++)
			{
				for (int i = 0; i <= marks; i++)
				{
					D3D11_QUERY_DESC desc = new() { Query = i == 0 ? D3D11_QUERY_TIMESTAMP_DISJOINT : D3D11_QUERY_TIMESTAMP };
					ID3D11Query* query;
					ThrowIfFailed(device->CreateQuery(&desc, &query));
					this.queries[s, i] = query;
				}
			}
		}
		catch
		{
			this.Dispose();
			throw;
		}
	}

	/// <summary>Smoothed milliseconds between consecutive marks</summary>
	public double[] Ms { get; }

	/// <summary>Starts a frame and sets mark 0</summary>
	public void Begin(ID3D11DeviceContext* context)
	{
		this.slot = (this.slot + 1) % Slots;
		if (this.pending[this.slot])
			this.Read(context);

		context->Begin((ID3D11Asynchronous*)this.queries[this.slot, 0]);
		this.Mark(context, 0);
	}

	public void Mark(ID3D11DeviceContext* context, int mark) => context->End((ID3D11Asynchronous*)this.queries[this.slot, mark + 1]);

	public void End(ID3D11DeviceContext* context)
	{
		context->End((ID3D11Asynchronous*)this.queries[this.slot, 0]);
		this.pending[this.slot] = true;
	}

	/// <summary>Never flushes, a result that isn't ready stays pending</summary>
	private void Read(ID3D11DeviceContext* context)
	{
		D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint;
		int marks = this.queries.GetLength(1) - 1;
		ulong* stamps = stackalloc ulong[marks];
		if (context->GetData((ID3D11Asynchronous*)this.queries[this.slot, 0], &disjoint, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT),
			    (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH) != 0)
			return;
		for (int i = 0; i < marks; i++)
		{
			if (context->GetData((ID3D11Asynchronous*)this.queries[this.slot, i + 1], stamps + i, sizeof(ulong),
				    (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH) != 0)
				return;
		}

		this.pending[this.slot] = false;
		if (disjoint.Disjoint)
			return;

		// Marks around a cross-queue fence wait can come back out of order, the sample is useless then
		for (int i = 1; i < marks; i++)
		{
			if (stamps[i] < stamps[i - 1])
				return;
		}

		for (int i = 0; i < this.Ms.Length; i++)
			this.Ms[i] = (this.Ms[i] * 0.9) + ((stamps[i + 1] - stamps[i]) * 1000.0 / disjoint.Frequency * 0.1);
	}

	public void Dispose()
	{
		foreach (ID3D11Query* query in this.queries)
			Dx.Release(query);
		GC.SuppressFinalize(this);
	}
}
