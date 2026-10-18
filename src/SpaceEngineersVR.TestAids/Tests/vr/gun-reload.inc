# One hand weapon's reload by hand, as a player does it, with the gun in the right hand. A gun comes out of the inventory
# empty, and the game loads it as it comes out (one of the three magazines, on its own reload timer). A magazine fired empty
# stays in the gun: the right stick click (the gun hand's) drops it, the left trigger, low at the hip, takes a fresh one from
# the pouch and, held, pushes it up into the well under the gun (gun-load.inc).
# Args: $1 the gun's item subtype, $2 its magazine's subtype.

plugin SpaceEngineersVR
world game:Earth Planet

# ---- What the world needs: the gun in toolbar slot 1 and three magazines in the inventory ----------------------------------

give PhysicalGunObject/$1
give AmmoMagazine/$2 3
toolbar 1 PhysicalGunObject/$1
log SpaceEngineersVR.log
expect session focused within 60
# out in the open, nothing within 40 m: the one shot below hits no block, so the world is kept for the next test
goto open 40

# ---- The gun into the right hand: the tools wheel's first slot ---------------------------------------------------------------

head 0 1.6 0
hand left -0.25 1.2 -0.3
hand right 0.25 1.3 -0.4 0 -5 0
wait 1
press left grip
wait 1
stick left 0 1
wait 0.5
release left grip
wait 0.3
stick left 0 0
wait 2
read character
expect weapon.item contains $1

# ---- Drawn empty, with magazines in the inventory: the game loads it, timed, and nothing drops ---------------------------------

expect log was drawn with its magazine empty; the game loads it within 10
expect log /Reload: done after .*[(]magazine [1-9]/ within 15
expect no log magazine dropped
expect no log stays in until the reload press

# ---- One shot, then the reload by hand: a magazine need not be empty to change it --------------------------------------------
# fired a little above level, over the open ground: lower, a rocket bursts on the ground a few metres off and hurts
log SpaceEngineersVR.log
hand right 0.25 1.3 -0.4 0 25 0
wait 0.5
press right trigger
wait 0.1
release right trigger
wait 3
hand right 0.25 1.3 -0.4 0 -5 0
wait 0.5
expect no log magazine dropped
include gun-load.inc
expect no log Exception
