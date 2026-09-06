# Open Configuration

Shared API for Vintage Story mods to handle configuration files (`ModConfig/.../*.json`)

## Features
- GUI section for admins to change the configuration of the mods
- If not admin show only client side mods configurations (server configurations are not send for security reasons)
- API for modders to add configurations and events
- Whenever you update the settings, supported mods will automatically update the variables.

## Not Supported
- Mods that does not use ``.json`` configuration files
- Mods that does not use open configuration for generating files does not automatically refresh upon saving (restart is required)