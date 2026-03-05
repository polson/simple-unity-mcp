import pytest
import json
from unity_mcp.server import _timeout_ms_from_command, compact


def test_compact_serialization():
    obj = {"success": True, "msg": "test"}
    result = compact(obj)
    assert result == '{"success":true,"msg":"test"}'


def test_timeout_parsing():
    # Default
    assert _timeout_ms_from_command({}) == 20000

    # Custom
    assert _timeout_ms_from_command({"timeout_ms": 5000}) == 5000

    # Min clamp
    assert _timeout_ms_from_command({"timeout_ms": 100}) == 1000

    # Invalid defaults to 20000
    assert _timeout_ms_from_command({"timeout_ms": "abc"}) == 20000
