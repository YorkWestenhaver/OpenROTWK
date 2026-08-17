// Bridge aliases for the step-4 LogicFrame move (api-freeze-v1 F6): the canonical types live
// in OpenSage.SimCore.Ticking.
global using LogicFrame = OpenSage.SimCore.Ticking.LogicFrame;
global using LogicFrameSpan = OpenSage.SimCore.Ticking.LogicFrameSpan;

// Step-5 (checksum framework): same bridge for the ObjectId / BitArray512 moves.
global using ObjectId = OpenSage.SimCore.Orders.ObjectId;
global using BitArray512 = OpenSage.SimCore.Numerics.BitArray512;
