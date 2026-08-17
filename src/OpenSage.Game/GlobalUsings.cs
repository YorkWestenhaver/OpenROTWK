// Bridge aliases for the step-4 LogicFrame move (api-freeze-v1 F6): the canonical types live
// in OpenSage.SimCore.Ticking; these aliases keep the ~90 existing consumers compiling without
// a mechanical using-churn across the project.
global using LogicFrame = OpenSage.SimCore.Ticking.LogicFrame;
global using LogicFrameSpan = OpenSage.SimCore.Ticking.LogicFrameSpan;
