# Third-party notices

## FixedMath.Net

`src/OpenSage.SimCore/Numerics/Fix64.cs` (the Q31.32 core representation and the
saturating `+`, `-`, `*` operators and rounding helpers) and the pure-integer
digit-by-digit restoring square root used as the reference in
`src/OpenSage.SimCore/Numerics/Fix64.Sqrt.cs` (widened to 128 bits) are vendored and
modified from FixedMath.Net:

- Source: https://github.com/asik/FixedMath.Net
  (commit `b2adac7713eda01fdd31578dd5a1d15f8f7ba067`)
- Copyright 2012 André Slupik
- License: Apache License, Version 2.0

> Licensed under the Apache License, Version 2.0 (the "License"); you may not use
> this file except in compliance with the License. You may obtain a copy of the
> License at
>
> http://www.apache.org/licenses/LICENSE-2.0
>
> Unless required by applicable law or agreed to in writing, software distributed
> under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
> CONDITIONS OF ANY KIND, either express or implied. See the License for the
> specific language governing permissions and limitations under the License.

FixedMath.Net itself incorporates code from the libfixmath library
(Copyright (C) 2012 Petteri Aimonen, MIT license) and the log2fix library
(Copyright (c) 2015 Dan Moulding, MIT license); see the upstream LICENSE.txt for
those texts. The log2fix-derived logarithm code is not part of the vendored subset.

Modifications made here are described in the header of
`src/OpenSage.SimCore/Numerics/Fix64.cs`.

(`OpenSage.Game` additionally references the unmodified FixedMath.NET 1.0.1 NuGet
package, unchanged from upstream OpenSAGE.)
