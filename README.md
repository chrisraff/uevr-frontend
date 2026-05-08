# UEVR.Frontend

The frontend injector for the UEVR mod. Does not contain the actual mod itself.

## This Fork

This fork adds a **background HID injection trigger**: bind any button on a HID gamepad or joystick, and UEVR will automatically inject into the target game when that button is pressed, including when the game is focused.

This feature is currently in its own branch as I may submit a PR once I am confident in the feature.

### How to use

1. In the UEVR frontend, click **Bind Button** and press the controller button you want to use.
2. Launch your game. When you're ready to inject, press the bound button.
3. UEVR will inject automatically. If no game is explicitly selected, it will inject into the first detected injectable process.

The binding is saved across sessions. Click **Clear** to remove it.
