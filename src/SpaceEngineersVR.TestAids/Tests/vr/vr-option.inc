# One of Cheezecake's SE VR's settings set through Options > VR, as a player sets it, from play. What it was is kept as
# {$4}, to set it back in the test's cleanup:
#   include vr-option.inc playPosition 0 selectedIndex was_playPosition
#   ...
#   cleanup
#   include vr-option.inc playPosition {was_playPosition} selectedIndex _
# $1 the setting's key (as in SpaceEngineersVR.cfg), $2 its value: a list's index, true or false for a box, a number for a
# slider; $3 the field of the control that holds it: selectedIndex, isChecked or value; $4 the name to keep the old one as.
# the game hands a key only to a screen that is fully open (one opening, unhiding or closing drops it): wait for it
read screen
expect state == OPENED within 15
key Escape
read screen
expect type == MyGuiScreenMainMenu within 5
click Options
read screen
expect type == MyGuiScreenOptionsSpace within 5
click VR
read screen
expect type == VROptionsScreen within 5
read control setting $1
remember $4 properties.$3
set "setting $1" $2
read control setting $1
expect properties.$3 == $2
# OK saves the settings and closes; Escape closes the game's Options and the menu
click OK
read screen
expect type == MyGuiScreenOptionsSpace within 5
read screen
expect state == OPENED within 15
key Escape
read screen
expect type == MyGuiScreenMainMenu within 5
read screen
expect state == OPENED within 15
key Escape
read screen
expect type == MyGuiScreenGamePlay within 5
