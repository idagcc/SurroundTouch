#!/usr/bin/env python3
"""Preserve Mono's version resource and explicitly embed/verify Windows manifests.

Mono mcs can silently ignore -win32manifest on non-Windows hosts. Windows build.ps1
uses Microsoft's compiler and does not need this cross-compilation helper.
"""
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET


def pe_resources(path):
    data = Path(path).read_bytes()
    pe = struct.unpack_from('<I', data, 60)[0]
    assert data[:2] == b'MZ' and data[pe:pe + 4] == b'PE\0\0', 'Invalid PE file'
    machine, sections = struct.unpack_from('<HH', data, pe + 4)
    assert machine == 0x8664, 'Expected Windows x64'
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    assert struct.unpack_from('<H', data, optional)[0] == 0x20b, 'Expected PE32+'

    def offset(rva):
        for index in range(sections):
            section = optional + optional_size + 40 * index
            extent, address, length, start = struct.unpack_from('<IIII', data, section + 8)
            if address <= rva < address + max(extent, length):
                return start + rva - address
        raise ValueError('Resource address is outside the PE sections')

    resource_rva = struct.unpack_from('<I', data, optional + 112 + 2 * 8)[0]
    base = offset(resource_rva)

    def entries(at):
        count = sum(struct.unpack_from('<HH', data, at + 12))
        return [struct.unpack_from('<II', data, at + 16 + index * 8) for index in range(count)]

    resources = []
    for kind, types in entries(base):
        assert not kind & 0x80000000, 'Named resource types are unsupported here'
        for name, languages in entries(base + (types & 0x7fffffff)):
            assert not name & 0x80000000, 'Named resources are unsupported here'
            for language, entry in entries(base + (languages & 0x7fffffff)):
                assert not entry & 0x80000000, 'Unexpected resource nesting'
                address, length = struct.unpack_from('<II', data, base + entry)
                start = offset(address)
                resources.append((kind, name, language, data[start:start + length]))
    return resources


def resource_entry(payload, kind, name, language=0):
    header = struct.pack('<IIHHHHIHHII', len(payload), 32, 0xffff, kind,
                         0xffff, name, 0, 0x1030, language, 0, 0)
    return header + payload + b'\0' * (-len(payload) % 4)


def build_resource(manifest_path, base_executable, destination):
    # The required null header precedes the actual .res entries.
    output = resource_entry(b'', 0, 0)
    for kind, name, language, payload in pe_resources(base_executable):
        if kind != 24:
            output += resource_entry(payload, kind, name, language)
    output += resource_entry(Path(manifest_path).read_bytes(), 24, 1)
    Path(destination).write_bytes(output)


def verify(executable, expected_ui_access):
    resources = pe_resources(executable)
    assert any(kind == 16 for kind, _, _, _ in resources), 'Missing file version resource'
    manifests = [payload for kind, name, _, payload in resources if kind == 24 and name == 1]
    assert len(manifests) == 1, 'Expected one executable manifest (RT_MANIFEST/1)'
    document = ET.fromstring(manifests[0])
    execution = next(node for node in document.iter() if node.tag.endswith('requestedExecutionLevel'))
    assert execution.attrib == {'level': 'asInvoker', 'uiAccess': expected_ui_access}, 'Incorrect UIAccess manifest'
    identity = next(node for node in document.iter() if node.tag.endswith('assemblyIdentity'))
    assert identity.attrib['version'] == '1.1.0.0', 'Incorrect version'
    assert identity.attrib['processorArchitecture'] == 'amd64', 'Incorrect manifest architecture'
    print(Path(executable).name + ': verified x64, file version resource, embedded UIAccess=' + expected_ui_access)


if __name__ == '__main__':
    if len(sys.argv) == 5 and sys.argv[1] == 'build':
        build_resource(*sys.argv[2:])
    elif len(sys.argv) == 4 and sys.argv[1] == 'verify':
        verify(*sys.argv[2:])
    else:
        raise SystemExit('Use: manifest_tools.py build manifest base.exe output.res | verify app.exe true|false')
