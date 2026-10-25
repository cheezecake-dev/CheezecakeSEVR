# Sample script

A programmable block script (`SevrSampleScript.cs`) that reads the block's hidden `SevrApi` property as an
`IReadOnlyDictionary<string, Delegate>` and, every 100 ticks, writes with `Echo` the API's `Version`, `IsActive`, `HudVisible`,
`HudDepth`, `HudTangents`, and whether `HudToWorld` is offered (it is not, to scripts). Every line starts `SevrSample: script:`.

## Use it

Paste the file into a programmable block's code (Edit), Check Code, OK. The world must allow in-game scripts. What it found
shows in the block's information, on the right of its terminal screen. A script runs in the game that simulates its grid: in a
game a player hosts, it reports the host's headset.
