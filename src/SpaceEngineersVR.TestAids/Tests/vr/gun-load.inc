# The magazine out and a fresh one in: the right stick click drops it, the left trigger at the hip takes another, and
# held, the left hand pushes it into the well. The well is where the plugin's line for the magazine taken from the pouch says
# the left hand seats it, in the STAGE space (the floor's), as the hand steps are.

# The click is held until the plugin has seen it: a tick of the game's longer than a short click would miss it.
press right stick
expect log /pressed RELOAD on the .*magazine [0-9]+[/]/ within 5
release right stick
wait 1
expect no log the game refuses
expect log magazine dropped within 5

hand left -0.2 1.0 -0.1
wait 1
press left trigger
wait 1
expect log from the pouch within 5

remember seat log '/seats with the other hand at stage \(([-\d.]+), ([-\d.]+), ([-\d.]+)\) m, yaw ([-\d.]+) pitch ([-\d.]+) roll ([-\d.]+)/' within 5
hand left {seat}
wait 1.5
expect log hand seated a magazine in the within 5
release left trigger
wait 5
expect log magazine is home within 5
expect log /Reload: done after .*[(]magazine [1-9]/ within 5
hand left -0.25 1.2 -0.3
wait 0.5
