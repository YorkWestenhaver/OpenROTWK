// Bridge alias for the step-5 BitArray512 move (api-freeze-v1 S4): the canonical type lives in
// OpenSage.SimCore.Numerics; BitArray<TEnum> and the rest of this assembly keep compiling
// without churn.
global using BitArray512 = OpenSage.SimCore.Numerics.BitArray512;
