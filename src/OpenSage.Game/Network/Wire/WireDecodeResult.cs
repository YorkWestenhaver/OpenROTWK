namespace OpenSage.Network.Wire;

/// <summary>
/// The result of a decode step: either a value, or a typed <see cref="WireDecodeStatus"/>
/// explaining why decode stopped. Every decode entry point in this directory returns one of
/// these (or a bare <see cref="WireDecodeStatus"/> where no value type is possible - see
/// <see cref="WireFrame.TryDecode"/>) instead of throwing on malformed input.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is meaningless whenever <see cref="Success"/> is false; callers must
/// check <see cref="Success"/> (or <see cref="Status"/>) before reading it.
/// </remarks>
public readonly struct WireDecodeResult<T>
{
    public WireDecodeStatus Status { get; }

    public T Value { get; }

    public bool Success => Status == WireDecodeStatus.Success;

    private WireDecodeResult(WireDecodeStatus status, T value)
    {
        Status = status;
        Value = value;
    }

    public static WireDecodeResult<T> Ok(T value) => new(WireDecodeStatus.Success, value);

    public static WireDecodeResult<T> Fail(WireDecodeStatus status)
    {
        if (status == WireDecodeStatus.Success)
        {
            throw new System.ArgumentException(
                "WireDecodeStatus.Success is not a failure status.", nameof(status));
        }

        return new WireDecodeResult<T>(status, default!);
    }
}
