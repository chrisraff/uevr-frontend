# UEVR.Frontend

The frontend injector for the UEVR mod. Does not contain the actual mod itself.

## This Fork

This fork adds a **background HID injection trigger**: bind any button on a HID gamepad or joystick, and UEVR will automatically inject into the target game when that button is pressed, including when the game is focused.

This feature is currently in its own branch as I may submit a PR once I am confident in the feature.

### How to use

1. In the UEVR frontend, click **Bind Button** and press the controller button you want to use.
2. Launch your game. When you're ready to inject, press the bound button.
3. UEVR will inject automatically — you don't need to have the game selected in the frontend first.

When choosing what to inject into, UEVR checks, in order:
1. Whichever window currently has focus, if it's a valid injection target (this covers the common case of pressing the button while actively playing).
2. Whatever is explicitly selected in the frontend's process list.
3. The last process you successfully injected into, even from a previous session.
4. The first detected injectable process, as a last resort.

The binding is saved across sessions. Click **Clear** to remove it.
