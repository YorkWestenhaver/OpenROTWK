// Bridge aliases for the step-4 LogicFrame move (api-freeze-v1 F6): the canonical types live
// in OpenSage.SimCore.Ticking; these aliases keep the ~90 existing consumers compiling without
// a mechanical using-churn across the project.
global using LogicFrame = OpenSage.SimCore.Ticking.LogicFrame;
global using LogicFrameSpan = OpenSage.SimCore.Ticking.LogicFrameSpan;

// Step-5 (checksum framework): ObjectId and BitArray512 are consumed by the frozen IXfer
// surface, so the types moved into OpenSage.SimCore (api-freeze-v1 S4).
global using ObjectId = OpenSage.SimCore.Orders.ObjectId;
global using BitArray512 = OpenSage.SimCore.Numerics.BitArray512;
