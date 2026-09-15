import json, re, sys, os
schema = json.load(open(sys.argv[1]))
golden = sys.argv[2]
ANNOT = {"$schema", "$id", "title", "$comment", "description", "format"}
KNOWN = ANNOT | {"$ref", "$defs", "type", "required", "properties", "enum", "const", "minimum", "maximum", "minLength", "maxLength",
                 "pattern", "items", "minItems", "maxItems", "oneOf", "anyOf", "allOf", "not", "if", "then", "else"}

def resolve(ref):
    assert ref.startswith("#/$defs/"), ref
    return schema["$defs"][ref.split("/")[-1]]

def typ(v):
    if v is None: return "null"
    if isinstance(v, bool): return "boolean"
    if isinstance(v, int): return "integer"
    if isinstance(v, float): return "integer" if v.is_integer() else "number"
    if isinstance(v, str): return "string"
    if isinstance(v, list): return "array"
    return "object"

def declared(s):
    names=set(s.get("properties",{}).keys())
    if "$ref" in s: names|=declared(resolve(s["$ref"]))
    for b in s.get("allOf",[]): names|=declared(b)
    return names

def valid(s, v, path="$", strict=True):
    errors = []
    for k in s:
        if k not in KNOWN: errors.append(f"{path}: unknown keyword {k}")
    if "$ref" in s: errors += valid(resolve(s["$ref"]), v, path, strict)
    if "type" in s:
        types = s["type"] if isinstance(s["type"], list) else [s["type"]]
        t = typ(v)
        if not (t in types or (t == "integer" and "number" in types)): errors.append(f"{path}: type {t} not in {types}")
    if "enum" in s and v not in s["enum"]: errors.append(f"{path}: {v!r} not in enum")
    if "const" in s and v != s["const"]: errors.append(f"{path}: {v!r} != const {s['const']!r}")
    if isinstance(v, (int, float)) and not isinstance(v, bool):
        if "minimum" in s and v < s["minimum"]: errors.append(f"{path}: below minimum")
        if "maximum" in s and v > s["maximum"]: errors.append(f"{path}: above maximum")
    if isinstance(v, str):
        if "minLength" in s and len(v) < s["minLength"]: errors.append(f"{path}: shorter than {s['minLength']}")
        if "maxLength" in s and len(v) > s["maxLength"]: errors.append(f"{path}: longer than {s['maxLength']}")
        if "pattern" in s and not re.search(s["pattern"], v): errors.append(f"{path}: pattern {s['pattern']} mismatch")
    if isinstance(v, list):
        if "minItems" in s and len(v) < s["minItems"]: errors.append(f"{path}: too few items")
        if "maxItems" in s and len(v) > s["maxItems"]: errors.append(f"{path}: too many items")
        if "items" in s:
            for i, item in enumerate(v): errors += valid(s["items"], item, f"{path}[{i}]", strict)
    if isinstance(v, dict):
        for r in s.get("required", []):
            if r not in v: errors.append(f"{path}: missing {r}")
        for name, sub in s.get("properties", {}).items():
            if name in v: errors += valid(sub, v[name], f"{path}.{name}", strict)
        if strict and "properties" in s:
            d = declared(s)
            errors += [f"{path}: undeclared {k}" for k in v if k not in d]
    if "allOf" in s:
        for sub in s["allOf"]: errors += valid(sub, v, path, False)
    if "anyOf" in s and not any(not valid(sub, v, path, strict) for sub in s["anyOf"]): errors.append(f"{path}: no anyOf branch")
    if "oneOf" in s:
        matches = [i for i, sub in enumerate(s["oneOf"]) if not valid(sub, v, path, strict)]
        if len(matches) != 1: errors.append(f"{path}: oneOf matched {matches}")
    if "not" in s and not valid(s["not"], v, path, False): errors.append(f"{path}: matched not")
    if "if" in s:
        if not valid(s["if"], v, path, False):
            if "then" in s: errors += valid(s["then"], v, path, False)
        elif "else" in s: errors += valid(s["else"], v, path, False)
    return errors

# every def and ref must resolve
def walk(node):
    if isinstance(node, dict):
        if "$ref" in node: resolve(node["$ref"])
        for value in node.values(): walk(value)
    elif isinstance(node, list):
        for value in node: walk(value)
walk(schema)
expected = {"commands": "clientCommandEnvelope", "client": "clientDeliveryAcknowledgement", "server": "serverEnvelope", "relay": "opaqueRelayFrame",
            "reconnect": None, "hello": None, "handshake": None}
bad = 0
for root, _, names in os.walk(golden):
    for name in sorted(names):
        folder = os.path.basename(root)
        if folder == "crypto": continue
        path = os.path.join(root, name)
        doc = json.load(open(path))
        # Strict mode proves every member is declared; lax mode proves the root oneOf is unambiguous
        # for readers that ignore unknown optional members.
        errors = valid(schema, doc) + valid(schema, doc, strict=False)
        if errors:
            bad += 1
            print(path); [print("   ", e) for e in errors[:12]]
print("invalid files:", bad)
