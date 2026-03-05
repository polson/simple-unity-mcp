# Simple Unity MCP

A Model Context Protocol (MCP) server integration for the Unity Editor, designed for seamless communication with LLMs to inspect and modify Unity prefabs.

## Features
- **Inspect Prefabs:** Read component structures and serialized values.
- **Execute Actions:** Create GameObjects, add components, set references, modify RectTransforms.
- **Search Prefabs:** Find prefabs by component type, name, or dependencies.
- **Play Mode Control:** Enter and exit Unity play mode from MCP.
- **Console Error Access:** Fetch recent Unity console errors and exceptions.
- **Transactional Safety:** Automatic rollbacks if an action fails.

## Installation

### 1) Install the Unity package
1. Open Unity.
2. Go to `Window -> Package Manager`.
3. Click the `+` button in the top left and select `Add package from git URL...`.
4. Enter: `https://github.com/polson/simple-unity-mcp.git?path=/unity`

*Note: You may need to change the repo URL to match your fork/account.*

### 2) Run the Python MCP server directly from GitHub (no clone)
The MCP server requires Python 3.10+ and one of these runners:

- `uvx` (recommended)
- `pipx` (alternative)

```bash
uvx --from "git+https://github.com/polson/simple-unity-mcp.git#subdirectory=python" unity-mcp-server --port 6789
```

Alternative with `pipx`:

```bash
pipx run --spec "git+https://github.com/polson/simple-unity-mcp.git#subdirectory=python" unity-mcp-server --port 6789
```

## Configuration

### Unity Editor Settings
Open `Window -> Unity MCP Settings` in the Unity Editor to change the WebSocket port (default: 6789), toggle auto-start, or manually start/stop the server.

### Claude Code one-liner

```bash
claude mcp add --transport stdio unity -- uvx --from "git+https://github.com/polson/simple-unity-mcp.git#subdirectory=python" unity-mcp-server --port 6789
```

After adding, restart Claude Code and run `/mcp` to verify server status.

### Generic JSON config (Claude Desktop, Cursor, and other stdio MCP clients)
Use this in the client's MCP config file/JSON UI:

```json
{
  "mcpServers": {
    "unity": {
      "command": "uvx",
      "args": [
        "--from",
        "git+https://github.com/polson/simple-unity-mcp.git#subdirectory=python",
        "unity-mcp-server",
        "--port",
        "6789"
      ]
    }
  }
}
```

### Project-scoped `.mcp.json` example
If your client supports project-level MCP config, add this at your project root:

```json
{
  "mcpServers": {
    "unity": {
      "command": "uvx",
      "args": [
        "--from",
        "git+https://github.com/polson/simple-unity-mcp.git#subdirectory=python",
        "unity-mcp-server",
        "--port",
        "6789"
      ]
    }
  }
}
```

### Compatibility quick reference

| Client type | Works with this repo now | Setup method |
| --- | --- | --- |
| MCP clients with local `stdio` support | Yes | Use `command: uvx` and the `--from git+...` args above |
| Claude Code | Yes | `claude mcp add --transport stdio ...` |
| Clients that only support remote HTTP/SSE MCP servers | Not yet | This repo currently ships a local stdio server |

## Tools Overview

- `unity_execute_actions`: Execute a sequence of Unity editor actions. Supports optional transactional rollback.
- `unity_execute_actions_safe_batch`: Execute actions transactionally with automatic rollback (apply all or none).
- `unity_preview_actions`: Dry-run a sequence of Unity editor actions and return planned changes without persisting edits.
- `unity_inspect_prefab`: Inspect a prefab and return discovered GameObjects, components, and serialized fields.
- `unity_get_object_reference`: Get a serialized object reference field value for a component in a prefab.
- `unity_assert_object_reference`: Assert a serialized object reference field points to an expected asset/path.
- `unity_search_prefabs`: Search prefabs by name, component presence, inheritance base prefab, or dependencies.
- `unity_execute_actions_on_prefab_variants`: Apply an action batch to all prefab variants inheriting from a base prefab.
- `unity_play_unity`: Enter Unity play mode.
- `unity_stop_unity`: Exit Unity play mode.
- `unity_get_console_errors`: Return recent Unity Console errors and exceptions.
- `unity_execute_menu_item`: Trigger a Unity Editor menu item by its path.
- `unity_ping_unity`: Ping the Unity WebSocket server to check connectivity.
- `unity_describe_actions`: List supported Unity WS commands, action names, and expected parameters.
