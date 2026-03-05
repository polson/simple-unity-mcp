"""
Unity MCP Server
Communicates with Unity Editor over WebSocket using compact JSON.
"""

import argparse
import asyncio
import json
import logging
import sys
from typing import Any

import websockets
from mcp import types
from mcp.server import Server
from mcp.server.stdio import stdio_server

DEFAULT_TIMEOUT_MS = 20000

# Global to hold the configured URL
_UNITY_WS_URL = "ws://127.0.0.1:6789"

app = Server("unity-mcp")

# Setup logging to stderr
logger = logging.getLogger("unity-mcp")


def compact(obj: dict[str, Any]) -> str:
    return json.dumps(obj, separators=(",", ":"))


def _timeout_ms_from_command(command: dict[str, Any]) -> int:
    raw = command.get("timeout_ms", DEFAULT_TIMEOUT_MS)
    try:
        timeout_ms = int(raw)
    except (TypeError, ValueError):
        timeout_ms = DEFAULT_TIMEOUT_MS
    return max(1000, timeout_ms)


async def send_to_unity(command: dict[str, Any]) -> dict[str, Any]:
    timeout_ms = _timeout_ms_from_command(command)
    timeout_seconds = (timeout_ms / 1000.0) + 2.0

    logger.debug(f"Sending command to Unity: {command.get('cmd')}")

    try:
        async with websockets.connect(_UNITY_WS_URL) as ws:
            await ws.send(compact(command))
            response = await asyncio.wait_for(ws.recv(), timeout=timeout_seconds)

            logger.debug("Received response from Unity")
            return json.loads(response)
    except ConnectionRefusedError:
        error_msg = f"Could not connect to Unity at {_UNITY_WS_URL}. Is the WebSocket server running?"
        logger.error(error_msg)
        return {
            "success": False,
            "error": error_msg,
            "cmd": command.get("cmd"),
        }
    except asyncio.TimeoutError:
        error_msg = "Unity did not respond before MCP client timeout."
        logger.error(error_msg)
        return {
            "success": False,
            "error": error_msg,
            "cmd": command.get("cmd"),
            "timeout_ms": timeout_ms,
        }
    except Exception as exc:
        logger.exception("Unexpected error sending to Unity")
        return {"success": False, "error": str(exc), "cmd": command.get("cmd")}


@app.list_tools()
async def list_tools() -> list[types.Tool]:
    return [
        types.Tool(
            name="execute_actions",
            description=(
                "Execute a sequence of Unity editor actions. Supports optional transactional "
                "rollback across touched prefabs."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "actions": {
                        "type": "array",
                        "minItems": 1,
                        "description": (
                            "Ordered action objects. Supported actions include: open_prefab, close_prefab, "
                            "save_prefab, create_game_object, add_component, set_rect_transform, set_image, "
                            "set_text_mesh_pro, set_object_reference."
                        ),
                        "items": {
                            "type": "object",
                            "properties": {
                                "action": {
                                    "type": "string",
                                    "description": "Action name, e.g. 'open_prefab'.",
                                }
                            },
                            "required": ["action"],
                            "additionalProperties": True,
                        },
                    },
                    "transactional": {
                        "type": "boolean",
                        "description": "If true, create prefab backups and roll back touched prefabs on failure.",
                        "default": False,
                    },
                    "rollback_on_failure": {
                        "type": "boolean",
                        "description": "Whether to restore backups if any action fails.",
                        "default": True,
                    },
                    "preview": {
                        "type": "boolean",
                        "description": "If true, run as dry-run and roll back after producing planned changes.",
                        "default": False,
                    },
                    "timeout_ms": {
                        "type": "integer",
                        "description": "Server-side command timeout in milliseconds.",
                        "minimum": 1000,
                    },
                },
                "required": ["actions"],
            },
        ),
        types.Tool(
            name="execute_actions_safe_batch",
            description=(
                "Execute actions transactionally with automatic rollback (apply all or none)."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "actions": {
                        "type": "array",
                        "minItems": 1,
                        "items": {
                            "type": "object",
                            "properties": {
                                "action": {"type": "string"},
                            },
                            "required": ["action"],
                            "additionalProperties": True,
                        },
                    },
                    "timeout_ms": {
                        "type": "integer",
                        "minimum": 1000,
                    },
                },
                "required": ["actions"],
            },
        ),
        types.Tool(
            name="preview_actions",
            description=(
                "Dry-run a sequence of Unity editor actions and return planned changes without persisting edits."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "actions": {
                        "type": "array",
                        "minItems": 1,
                        "items": {
                            "type": "object",
                            "properties": {
                                "action": {"type": "string"},
                            },
                            "required": ["action"],
                            "additionalProperties": True,
                        },
                    },
                    "rollback_on_failure": {"type": "boolean", "default": True},
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
                "required": ["actions"],
            },
        ),
        types.Tool(
            name="inspect_prefab",
            description=(
                "Inspect a prefab and return discovered GameObjects, components, and serialized fields."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "prefab_path": {"type": "string"},
                    "target_path": {
                        "type": "string",
                        "description": "Optional specific GameObject path inside prefab.",
                    },
                    "include_serialized_values": {
                        "type": "boolean",
                        "default": False,
                    },
                    "timeout_ms": {
                        "type": "integer",
                        "minimum": 1000,
                    },
                },
                "required": ["prefab_path"],
            },
        ),
        types.Tool(
            name="get_object_reference",
            description=(
                "Get a serialized object reference field value for a component in a prefab."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "prefab_path": {"type": "string"},
                    "target_path": {"type": "string"},
                    "component_type": {"type": "string"},
                    "component_index": {"type": "integer", "minimum": 0},
                    "property_path": {"type": "string"},
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
                "required": ["prefab_path", "target_path", "property_path"],
            },
        ),
        types.Tool(
            name="assert_object_reference",
            description=(
                "Assert a serialized object reference field points to an expected asset/path."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "prefab_path": {"type": "string"},
                    "target_path": {"type": "string"},
                    "component_type": {"type": "string"},
                    "component_index": {"type": "integer", "minimum": 0},
                    "property_path": {"type": "string"},
                    "expected_asset_path": {"type": "string"},
                    "expected_target_path": {"type": "string"},
                    "expected_component_type": {"type": "string"},
                    "expected_null": {"type": "boolean"},
                    "fail_on_mismatch": {"type": "boolean", "default": True},
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
                "required": ["prefab_path", "target_path", "property_path"],
            },
        ),
        types.Tool(
            name="search_prefabs",
            description=(
                "Search prefabs by name, component presence, inheritance base prefab, or dependencies."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "root_folder": {"type": "string", "default": "Assets"},
                    "name_contains": {"type": "string"},
                    "component_type": {"type": "string"},
                    "base_prefab_path": {
                        "type": "string",
                        "description": "Match prefab variants inheriting from this prefab.",
                    },
                    "references_asset_path": {
                        "type": "string",
                        "description": "Match prefabs whose dependencies include this asset path.",
                    },
                    "include_children": {"type": "boolean", "default": True},
                    "limit": {"type": "integer", "minimum": 1},
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
            },
        ),
        types.Tool(
            name="describe_actions",
            description=(
                "List supported Unity WS commands, action names, and expected parameters."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
            },
        ),
        types.Tool(
            name="execute_actions_on_prefab_variants",
            description=(
                "Apply an action batch to all prefab variants inheriting from a base prefab."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "base_prefab_path": {"type": "string"},
                    "actions": {
                        "type": "array",
                        "minItems": 1,
                        "items": {
                            "type": "object",
                            "properties": {"action": {"type": "string"}},
                            "required": ["action"],
                            "additionalProperties": True,
                        },
                    },
                    "root_folder": {"type": "string", "default": "Assets"},
                    "include_base": {"type": "boolean", "default": False},
                    "limit": {"type": "integer", "minimum": 1},
                    "transactional": {"type": "boolean", "default": True},
                    "rollback_on_failure": {"type": "boolean", "default": True},
                    "preview": {"type": "boolean", "default": False},
                    "stop_on_failure": {"type": "boolean", "default": False},
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
                "required": ["base_prefab_path", "actions"],
            },
        ),
        types.Tool(
            name="play_unity",
            description="Enter Unity play mode.",
            inputSchema={
                "type": "object",
                "properties": {
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
            },
        ),
        types.Tool(
            name="stop_unity",
            description="Exit Unity play mode.",
            inputSchema={
                "type": "object",
                "properties": {
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
            },
        ),
        types.Tool(
            name="get_console_errors",
            description="Return recent Unity Console errors and exceptions.",
            inputSchema={
                "type": "object",
                "properties": {
                    "limit": {
                        "type": "integer",
                        "minimum": 1,
                        "description": "Maximum number of errors to return.",
                    },
                    "include_stack_trace": {
                        "type": "boolean",
                        "default": True,
                        "description": "Include stack traces for each error.",
                    },
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
            },
        ),
        types.Tool(
            name="ping_unity",
            description="Ping the Unity WebSocket server to check connectivity.",
            inputSchema={"type": "object", "properties": {}},
        ),
        types.Tool(
            name="execute_menu_item",
            description="Trigger a Unity Editor menu item by its path.",
            inputSchema={
                "type": "object",
                "properties": {
                    "menu_path": {
                        "type": "string",
                        "description": "e.g. 'Tools/MyTool/Run'",
                    },
                    "timeout_ms": {"type": "integer", "minimum": 1000},
                },
                "required": ["menu_path"],
            },
        ),
    ]


def _with_timeout_arg(
    command: dict[str, Any], arguments: dict[str, Any]
) -> dict[str, Any]:
    if "timeout_ms" in arguments:
        command["timeout_ms"] = arguments["timeout_ms"]
    return command


@app.call_tool()
async def call_tool(
    name: str, arguments: dict[str, Any] | None
) -> list[types.TextContent]:
    logger.info(f"Tool called: {name}")
    args = arguments or {}
    if name == "ping_unity":
        result = await send_to_unity(_with_timeout_arg({"cmd": "ping"}, args))
    elif name == "play_unity":
        result = await send_to_unity(_with_timeout_arg({"cmd": "play"}, args))
    elif name == "stop_unity":
        result = await send_to_unity(_with_timeout_arg({"cmd": "stop"}, args))
    elif name == "get_console_errors":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "get_console_errors",
                    "limit": args.get("limit", 50),
                    "include_stack_trace": bool(args.get("include_stack_trace", True)),
                },
                args,
            )
        )
    elif name == "execute_actions":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "execute_actions",
                    "actions": args["actions"],
                    "transactional": bool(args.get("transactional", False)),
                    "rollback_on_failure": bool(args.get("rollback_on_failure", True)),
                    "preview": bool(args.get("preview", False)),
                },
                args,
            )
        )
    elif name == "preview_actions":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "preview_actions",
                    "actions": args["actions"],
                    "rollback_on_failure": bool(args.get("rollback_on_failure", True)),
                },
                args,
            )
        )
    elif name == "execute_actions_safe_batch":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "execute_actions",
                    "actions": args["actions"],
                    "transactional": True,
                    "rollback_on_failure": True,
                    "preview": False,
                },
                args,
            )
        )
    elif name == "inspect_prefab":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "inspect_prefab",
                    "prefab_path": args["prefab_path"],
                    "target_path": args.get("target_path"),
                    "include_serialized_values": bool(
                        args.get("include_serialized_values", False)
                    ),
                },
                args,
            )
        )
    elif name == "get_object_reference":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "get_object_reference",
                    "prefab_path": args["prefab_path"],
                    "target_path": args["target_path"],
                    "component_type": args.get("component_type"),
                    "component_index": args.get("component_index"),
                    "property_path": args["property_path"],
                },
                args,
            )
        )
    elif name == "assert_object_reference":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "assert_object_reference",
                    "prefab_path": args["prefab_path"],
                    "target_path": args["target_path"],
                    "component_type": args.get("component_type"),
                    "component_index": args.get("component_index"),
                    "property_path": args["property_path"],
                    "expected_asset_path": args.get("expected_asset_path"),
                    "expected_target_path": args.get("expected_target_path"),
                    "expected_component_type": args.get("expected_component_type"),
                    "expected_null": args.get("expected_null"),
                    "fail_on_mismatch": bool(args.get("fail_on_mismatch", True)),
                },
                args,
            )
        )
    elif name == "search_prefabs":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "search_prefabs",
                    "root_folder": args.get("root_folder", "Assets"),
                    "name_contains": args.get("name_contains"),
                    "component_type": args.get("component_type"),
                    "base_prefab_path": args.get("base_prefab_path"),
                    "references_asset_path": args.get("references_asset_path"),
                    "include_children": bool(args.get("include_children", True)),
                    "limit": args.get("limit", 200),
                },
                args,
            )
        )
    elif name == "describe_actions":
        result = await send_to_unity(
            _with_timeout_arg({"cmd": "describe_actions"}, args)
        )
    elif name == "execute_actions_on_prefab_variants":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "execute_actions_on_prefab_variants",
                    "base_prefab_path": args["base_prefab_path"],
                    "actions": args["actions"],
                    "root_folder": args.get("root_folder", "Assets"),
                    "include_base": bool(args.get("include_base", False)),
                    "limit": args.get("limit", 200),
                    "transactional": bool(args.get("transactional", True)),
                    "rollback_on_failure": bool(args.get("rollback_on_failure", True)),
                    "preview": bool(args.get("preview", False)),
                    "stop_on_failure": bool(args.get("stop_on_failure", False)),
                },
                args,
            )
        )
    elif name == "execute_menu_item":
        result = await send_to_unity(
            _with_timeout_arg(
                {
                    "cmd": "execute_menu_item",
                    "menu_path": args["menu_path"],
                },
                args,
            )
        )
    else:
        logger.warning(f"Unknown tool requested: {name}")
        result = {"success": False, "error": f"Unknown tool: {name}"}

    return [types.TextContent(type="text", text=compact(result))]


async def _run_server() -> None:
    logger.info("Starting Unity MCP server stdio listener")
    async with stdio_server() as (read_stream, write_stream):
        await app.run(read_stream, write_stream, app.create_initialization_options())


def main():
    parser = argparse.ArgumentParser(description="Unity MCP Server")
    parser.add_argument(
        "--host", default="127.0.0.1", help="Unity WebSocket server host"
    )
    parser.add_argument(
        "--port", type=int, default=6789, help="Unity WebSocket server port"
    )
    parser.add_argument(
        "--check-unity",
        action="store_true",
        help="Ping Unity and exit with status 0 or 1",
    )
    parser.add_argument("--debug", action="store_true", help="Enable debug logging")

    args = parser.parse_args()

    # Configure logging
    log_level = logging.DEBUG if args.debug else logging.INFO
    logging.basicConfig(
        level=log_level, stream=sys.stderr, format="%(levelname)s: %(message)s"
    )

    global _UNITY_WS_URL
    _UNITY_WS_URL = f"ws://{args.host}:{args.port}"

    if args.check_unity:
        logger.info(f"Checking Unity connection at {_UNITY_WS_URL}...")
        result = asyncio.run(send_to_unity({"cmd": "ping", "timeout_ms": 3000}))
        if result.get("success"):
            logger.info("Successfully connected to Unity.")
            sys.exit(0)
        else:
            logger.error(f"Failed to connect to Unity: {result.get('error')}")
            sys.exit(1)

    # Run the MCP server
    asyncio.run(_run_server())


if __name__ == "__main__":
    main()
