"""Writes MunShot's localization file, Assets/MunShot/Copied/localizations/munshot_localization.csv.

The English text, and a note for translators, is in `rows` below; the other 11 languages are in translations.py.
Edit them here, never the CSV by hand. Checks before writing: every key has all 11 translations, every translation has
the English text's {0}-style placeholders, and translations.py has no keys that aren't in `rows`.
Run: python Tools/Localization/make_localization.py
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from translations import T, POSE_ENGLISH

CSV = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..',
                   'Assets', 'MunShot', 'Copied', 'localizations', 'munshot_localization.csv')

rows = [
    ("MunShot/UI/Title", "Overlay title and app tray tooltip", "Photo Mode"),
    ("Menu/Escape/MunShotPhotoMode", "Pause menu button that opens photo mode (the game looks pause menu text up under Menu/Escape/)", "Photo Mode"),
    ("MunShot/UI/Kerbals", "Tab: the kerbals in the shot", "Kerbals"),
    ("MunShot/UI/Pose", "Tab: posing and moving the selected kerbal", "Pose"),
    ("MunShot/UI/Camera", "Tab: the photo camera", "Camera"),
    ("MunShot/UI/BringInKerbal", "Button: opens the list of kerbals to put in front of the camera", "Bring in a kerbal"),
    ("MunShot/UI/InThisScene", "Kerbal list section: kerbals in the scene when photo mode opened", "In this scene"),
    ("MunShot/UI/Elsewhere", "Kerbal list section: every other kerbal in the roster", "Elsewhere"),
    ("MunShot/UI/NoKerbals", "Kerbal list when no kerbals were found", "No kerbals found"),
    ("MunShot/UI/EveryoneIsIn", "Kerbal list when every kerbal has been brought in", "Everyone is in"),
    ("MunShot/UI/NobodyIn", "Shown when no kerbal has been brought in yet", "Nobody brought in yet"),
    ("MunShot/UI/AddKerbalFirst", "Pose tab, shown instead of its controls until a kerbal has been brought in", "Add a kerbal first"),
    ("MunShot/UI/NoPose", "Pose list entry: the kerbal's own animation, no held pose", "None"),
    ("MunShot/UI/Helmet", "Toggle: the selected kerbal wears their helmet", "Helmet"),
    ("MunShot/UI/Visor", "Toggle: the helmet's visor glass is shown", "Visor"),
    ("MunShot/UI/Move", "Tool button: move the selected kerbal with the VAB's arrows", "Move"),
    ("MunShot/UI/Rotate", "Tool button: rotate the selected kerbal with the VAB's rings", "Rotate"),
    ("MunShot/UI/Jetpack", "Switch on the Pose tab: the kerbal's jetpack deployed for flying, or folded away", "Jetpack"),
    ("MunShot/UI/AngleSnap", "Toggle: rings turn in steps, as the VAB's angle snap", "Angle snap"),
    ("MunShot/UI/SnapToSurface", "Button: stand the selected kerbal on the surface just below", "Snap to surface"),
    ("MunShot/UI/StandUpright", "Button: turn the selected kerbal upright on screen (head towards the top of the picture)", "Stand upright"),
    ("MunShot/UI/Free", "Camera mode button: fly the camera freely", "Free"),
    ("MunShot/UI/Orbit", "Camera mode button: circle a vessel or kerbal", "Orbit"),
    ("MunShot/UI/OrbitAround", "Caption above the list of things the camera can orbit", "Orbit around"),
    ("MunShot/UI/FieldOfView", "Caption above the camera's field of view slider", "Field of view"),
    ("MunShot/UI/Color", "Photo mode tab: the picture's exposure, contrast, saturation and warmth (keep it short: one of four tabs)", "Color"),
    ("MunShot/UI/Light", "Photo mode tab: photo mode's own lights (keep it short: one of five tabs)", "Light"),
    ("MunShot/UI/NoLights", "Light tab: shown in the empty list of lights", "No lights yet."),
    ("MunShot/UI/AddLight", "Button: add a light in front of the camera, shining where it looks", "Add light"),
    ("MunShot/UI/LightName", "A light's name in the list; {0} is its number", "Light {0}"),
    ("MunShot/UI/PointLight", "Button: a light that shines all around, like a lamp", "Point"),
    ("MunShot/UI/SpotLight", "Button: a light that shines in a cone, like a torch", "Spot"),
    ("MunShot/UI/LightBrightness", "Caption above the slider for how bright the light is", "Brightness"),
    ("MunShot/UI/LightRange", "Caption above the slider for how far the light reaches", "Range"),
    ("MunShot/UI/SpotAngle", "Caption above the slider for how wide a spot light's cone is", "Cone width"),
    ("MunShot/UI/KerbalsOnly", "Switch: the light lights only kerbals, not ships or the ground", "Kerbals only"),
    ("MunShot/UI/Exposure", "Caption above the slider that makes the whole picture brighter or darker", "Exposure"),
    ("MunShot/UI/Contrast", "Caption above the slider for more or less difference between light and dark", "Contrast"),
    ("MunShot/UI/Saturation", "Caption above the slider for more or less colourful (lowest is black and white)", "Saturation"),
    ("MunShot/UI/Warmth", "Caption above the slider that makes the picture warmer (orange) or cooler (blue)", "Warmth"),
    ("MunShot/UI/ResetColor", "Button: put exposure, contrast, saturation and warmth back to normal", "Reset"),
    ("MunShot/UI/Fisheye", "Caption above the slider that bulges the picture out from its middle, like a fisheye lens", "Fisheye"),
    ("MunShot/UI/DepthOfField", "Switch: blur what's nearer and further than the focus distance, like a real camera lens", "Depth of field"),
    ("MunShot/UI/FocusDistance", "Caption above the slider for how far from the camera things are sharp", "Focus distance"),
    ("MunShot/UI/Blur", "Caption above the slider for how strongly what's out of focus is blurred (the lens aperture)", "Blur"),
    ("MunShot/UI/FocusOnSelected", "Button: set the focus distance to the selected kerbal (turns depth of field on)", "Focus on selected kerbal"),
    ("MunShot/UI/KeyRightMouse", "Hint bar: the key part for holding the right mouse button", "Hold right mouse"),
    ("MunShot/UI/KeyWheel", "Hint bar: the key part for the mouse wheel", "Mouse wheel"),
    ("MunShot/UI/HintFly", "Hint bar: what the fly keys do", "Fly"),
    ("MunShot/UI/HintLook", "Hint bar: what holding right mouse does in free camera", "Look"),
    ("MunShot/UI/HintUpDown", "Hint bar: what the mouse wheel does in free camera", "Up/down"),
    ("MunShot/UI/HintCircle", "Hint bar: what holding right mouse does in orbit camera", "Circle"),
    ("MunShot/UI/HintCloser", "Hint bar: what the mouse wheel does in orbit camera", "Closer/further"),
    ("MunShot/UI/HintRoll", "Hint bar: what the roll keys do", "Roll"),
    ("MunShot/UI/HintLevel", "Hint bar: what the level horizon key does (turns the camera back to a level horizon)", "Level"),
    ("MunShot/UI/HintTurn", "Hint bar, while dragging kerbals: holding the right button too turns them", "Turn"),
    ("MunShot/UI/HintLean", "Hint bar, while dragging kerbals: holding this key leans them to the side", "Lean"),
    ("MunShot/UI/HintTip", "Hint bar, while dragging kerbals: holding these keys tips them forward and back", "Tip forward/back"),
    ("MunShot/UI/KeyDrag", "Hint bar: the key part for dragging the mouse on empty space", "Drag"),
    ("MunShot/UI/KeyShift", "Hint bar keycap: the Shift key, as printed on keyboards in this language", "Shift"),
    ("MunShot/UI/KeyCtrl", "Hint bar keycap: the Control key, as printed on keyboards in this language", "Ctrl"),
    ("MunShot/UI/KeyAlt", "Hint bar keycap: the Alt key, as printed on keyboards in this language", "Alt"),
    ("MunShot/UI/KeySpace", "Hint bar keycap: the space bar, as printed on keyboards in this language", "Space"),
    ("MunShot/UI/KeyEnter", "Hint bar keycap: the Enter (Return) key, as printed on keyboards in this language", "Enter"),
    ("MunShot/UI/KeyEsc", "Hint bar keycap: the Escape key, as printed on keyboards in this language", "Esc"),
    ("MunShot/UI/KeyNumpad", "Hint bar keycap: a key on the number pad; {0} is the key (5, Plus, ...)", "Num {0}"),
    ("MunShot/UI/HintSelectSeveral", "Hint bar: what dragging a box does (selects several kerbals)", "Select several"),
    ("MunShot/UI/HintRemove", "Hint bar: what the remove key does (takes the highlighted kerbals out)", "Remove"),
    ("MunShot/Status/AllBack", "Photo mode note after removing several kerbals; {0} is how many", "{0} kerbals are back where they were."),
    ("MunShot/Status/AllOnSurface", "Photo mode note after Snap to surface on several kerbals; {0} is how many", "{0} kerbals are standing on the surface."),
    ("MunShot/Status/AllUpright", "Photo mode note after Stand upright on several kerbals; {0} is how many", "{0} kerbals are standing upright."),
    ("MunShot/Settings/PhotoMode", "Mod settings: heading of the photo mode settings", "Photo mode"),
    ("MunShot/Settings/PhotoCamera", "Mod settings: heading of the photo camera settings", "Photo camera"),
    ("MunShot/Settings/TogglePhotoMode", "Mod settings: the keyboard shortcut that opens and closes photo mode", "Open or close photo mode"),
    ("MunShot/Settings/Description/TogglePhotoMode", "Mod settings: explains the photo mode shortcut", "Opens and closes photo mode in flight. It's also in the app tray and the pause menu."),
    ("MunShot/Settings/HideUi", "Mod settings: the key that hides photo mode's UI", "Hide photo mode UI"),
    ("MunShot/Settings/Description/HideUi", "Mod settings: explains the hide UI key", "Hides photo mode's panels and handles to frame the shot; press again to bring them back."),
    ("MunShot/Settings/RemoveKerbal", "Mod settings: the key that takes kerbals out of the shot", "Remove kerbal"),
    ("MunShot/Settings/Description/RemoveKerbal", "Mod settings: explains the remove key", "Takes the highlighted kerbals out of the shot; the real ones show again where they are."),
    ("MunShot/Settings/RemoveKerbalAlternate", "Mod settings: a second key for removing kerbals", "Remove kerbal (second key)"),
    ("MunShot/Settings/Description/RemoveKerbalAlternate", "Mod settings: explains the second remove key", "A second key for Remove kerbal."),
    ("MunShot/Settings/FlySpeed", "Mod settings: the free camera's speed", "Fly speed"),
    ("MunShot/Settings/Description/FlySpeed", "Mod settings: explains fly speed", "How fast the free camera flies, in metres per second. Hold Faster for five times this, doubling every second it stays held (up to 2000 m/s); Slower for a fifth."),
    ("MunShot/Settings/FlyForward", "Mod settings: key", "Fly forward"),
    ("MunShot/Settings/Description/FlyForward", "Mod settings: explains the key", "Free camera: fly forward."),
    ("MunShot/Settings/FlyBack", "Mod settings: key", "Fly back"),
    ("MunShot/Settings/Description/FlyBack", "Mod settings: explains the key", "Free camera: fly backward."),
    ("MunShot/Settings/FlyLeft", "Mod settings: key", "Fly left"),
    ("MunShot/Settings/Description/FlyLeft", "Mod settings: explains the key", "Free camera: fly left."),
    ("MunShot/Settings/FlyRight", "Mod settings: key", "Fly right"),
    ("MunShot/Settings/Description/FlyRight", "Mod settings: explains the key", "Free camera: fly right."),
    ("MunShot/Settings/RollLeft", "Mod settings: key", "Roll left"),
    ("MunShot/Settings/Description/RollLeft", "Mod settings: explains the key", "Tilts the photo camera to the left."),
    ("MunShot/Settings/RollRight", "Mod settings: key", "Roll right"),
    ("MunShot/Settings/Description/RollRight", "Mod settings: explains the key", "Tilts the photo camera to the right."),
    ("MunShot/Settings/TiltWhileDragging", "Mod settings: key", "Tilt while dragging"),
    ("MunShot/Settings/Description/TiltWhileDragging", "Mod settings: explains the key", "Hold while dragging kerbals to lean them to the side with the mouse. Hold Alt as well to tip them forward and back instead. Holding the right mouse button turns them."),
    ("MunShot/Settings/TipWhileDragging", "Mod settings: key", "Tip while dragging"),
    ("MunShot/Settings/Description/TipWhileDragging", "Mod settings: explains the key", "Hold with Tilt while dragging to tip the dragged kerbals forward and back with the mouse."),
    ("MunShot/Settings/LevelHorizon", "Mod settings: key", "Level horizon"),
    ("MunShot/Settings/Description/LevelHorizon", "Mod settings: explains the key", "Turns the photo camera back to a level horizon, still looking the same way."),
    ("MunShot/Settings/Fast", "Mod settings: the faster key (also adds to the selection and gives finer ring steps)", "Faster"),
    ("MunShot/Settings/Description/Fast", "Mod settings: explains the faster key", "Hold to move the photo camera five times faster, speeding up the longer it's held while flying. Also adds kerbals to the selection when clicking or dragging a box, and turns rings in finer steps."),
    ("MunShot/Settings/Slow", "Mod settings: key", "Slower"),
    ("MunShot/Settings/Description/Slow", "Mod settings: explains the key", "Hold to move the photo camera five times slower."),
    ("MunShot/Status/CameraUnavailable", "Photo mode note when it can't take over the game's camera, so it doesn't open", "Photo mode can't take over the camera right now; see the log."),
    ("MunShot/UI/HintHideUi", "Hint bar: what the hide UI key does", "Hide UI"),
    ("MunShot/UI/HintTakePhoto", "Hint bar: what the screenshot key does", "Take photo"),
    ("MunShot/UI/HintExit", "Hint bar: what the photo mode key does", "Exit"),
    ('MunShot/Status/AlreadyIn', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is already in.'),
    ('MunShot/Status/AlreadyInSelected', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is already in; selected.'),
    ('MunShot/Status/BackWhereTheyWere', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is back where they were.'),
    ('MunShot/Status/BringKerbalFirst', 'Photo mode note shown at the top of the screen', 'Bring a kerbal in first.'),
    ('MunShot/Status/Bringing', 'Photo mode note shown at the top of the screen; {0} is a name', 'Bringing in {0}…'),
    ('MunShot/Status/BuildFailed', 'Photo mode note shown at the top of the screen; {0} is a name', "Couldn't build {0}'s double; see the log."),
    ('MunShot/Status/CameraOff', 'Photo mode note shown at the top of the screen', "The photo camera isn't on."),
    ('MunShot/Status/CaptureNotRunning', 'Photo mode note shown at the top of the screen', "Can't take a photo: the game's screenshot capture isn't running. See the log."),
    ('MunShot/Status/CouldNotHold', 'Photo mode note shown at the top of the screen; {0} is a name', "Couldn't hold {0}; see the log."),
    ('MunShot/Status/DoublesNotLoaded', 'Photo mode note shown at the top of the screen', "Couldn't load what photo doubles are built from; see the log."),
    ('MunShot/Status/FlyingFree', 'Photo mode note shown at the top of the screen', 'Flying free.'),
    ('MunShot/Status/LightAdded', 'Photo mode note after adding a light; {0} is its name', '{0} added in front of the camera.'),
    ('MunShot/Status/LightRemoved', 'Photo mode note after taking a light away; {0} is its name', '{0} removed.'),
    ('MunShot/Status/LightsRemoved', 'Photo mode note after taking several lights away; {0} is how many', '{0} lights removed.'),
    ('MunShot/Status/LightsFull', 'Photo mode note when no more lights can be added; {0} is how many there can be', 'Up to {0} lights.'),
    ('MunShot/Status/FocusBehind', 'Photo mode note after Focus on selected kerbal; {0} is a name', "{0} isn't in front of the camera."),
    ('MunShot/Status/FocusedOn', 'Photo mode note after Focus on selected kerbal; {0} is a name', '{0} is in focus.'),
    ('MunShot/Status/InFrontOfCamera', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is in front of the camera.'),
    ('MunShot/Status/MaybeNotSaved', 'Photo mode note shown at the top of the screen', 'The photo may not have been saved; see the log.'),
    ('MunShot/Status/NoActiveVessel', 'Photo mode note shown at the top of the screen; {0} is a name', "Can't bring {0} in without an active vessel; see the log."),
    ('MunShot/Status/NoCamera', 'Photo mode note shown at the top of the screen', "There's no camera to place the kerbal in front of."),
    ('MunShot/Status/NotSaved', 'Photo mode note shown at the top of the screen', "The photo wasn't saved; see the log."),
    ('MunShot/Status/NothingBelow', 'Photo mode note: {0} is a distance in metres, {1} a kerbal name', 'Nothing to stand on within {0:0.#} m below {1}.'),
    ('MunShot/Status/OnSurface', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is standing on the surface.'),
    ('MunShot/Status/OnlyInFlight', 'Photo mode note shown at the top of the screen', 'Photo mode is only available in flight.'),
    ('MunShot/Status/OrbitTargetGone', 'Photo mode note shown at the top of the screen; {0} is a name', "{0} isn't there to orbit any more."),
    ('MunShot/Status/Orbiting', 'Photo mode note shown at the top of the screen; {0} is a name', 'Orbiting {0}.'),
    ('MunShot/Status/PickOrbitTarget', 'Photo mode note shown at the top of the screen', 'Pick something to orbit first.'),
    ('MunShot/Status/Saved', 'Photo mode note after a photo is saved; {0} is the file path', 'Saved {0}'),
    ('MunShot/Status/Selected', 'Photo mode note shown at the top of the screen; {0} is a name', 'Selected {0}.'),
    ('MunShot/Status/SnapNobody', 'Photo mode note shown at the top of the screen', 'Bring a kerbal in first; Snap to surface moves the selected kerbal.'),
    ('MunShot/Status/StillBuilding', 'Photo mode note shown at the top of the screen', 'The double is still being built.'),
    ('MunShot/Status/Upright', 'Photo mode note shown at the top of the screen; {0} is a name', '{0} is standing upright.'),
    ('MunShot/Status/UprightNobody', 'Photo mode note shown at the top of the screen', 'Bring a kerbal in first; Stand upright turns the selected kerbal.'),
    ('MunShot/UI/ActiveVessel', 'Orbit list entry for the active vessel; {0} is its name', '{0} (active vessel)'),
]
langs = ["English", "German", "French", "Italian", "Spanish", "Japanese", "Korean", "Polish", "Russian",
         "Chinese (Simplified)", "Portuguese (Brazil)", "Chinese (Traditional)"]


def q(v):
    return '"' + v.replace('"', '""') + '"' if any(c in v for c in ',"{') else v


# The pose names, translated by preset id (PosePreset.DisplayName)
for pose_id, english in POSE_ENGLISH.items():
    rows.append((f"MunShot/Pose/{pose_id}", "Pose name in the pose list", english))

placeholder = re.compile(r"\{[0-9][^}]*\}")
problems = []
lines = ["Key,Type,Desc," + ",".join(langs)]
for k, d, e in rows:
    others = T.get(k)
    if others is None:
        problems.append(f"{k}: no translations")
        others = [e] * (len(langs) - 1)
    if len(others) != len(langs) - 1:
        problems.append(f"{k}: {len(others)} translations, expected {len(langs) - 1}")
    for lang, text in zip(langs[1:], others):
        if sorted(placeholder.findall(text)) != sorted(placeholder.findall(e)):
            problems.append(f"{k} [{lang}]: placeholders {placeholder.findall(text)} vs English {placeholder.findall(e)}")
    lines.append(",".join([k, "Text", q(d), q(e)] + [q(t) for t in others]))
for extra in sorted(set(T) - {k for k, _, _ in rows}):
    problems.append(f"{extra}: translated but not a key")
if problems:
    print("\n".join(problems))
    raise SystemExit(1)
open(CSV, 'w', encoding='utf-8', newline='\r\n').write("\n".join(lines) + "\n")
print(len(rows), 'keys')
