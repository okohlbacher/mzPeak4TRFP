import sys
import io
from collections import deque

import psims

type_name = sys.argv[1]
queries = sys.argv[2:]

cv = psims.load_psims()

queue = deque(queries)
seen = set()
enum_buf = io.StringIO()
method_buf = io.StringIO()
enum_name = type_name
enum_buf.write(f"""public enum {enum_name} {{
""")
name_map = {}
id_map = {}
method_buf.write(f"""
public static class {enum_name}Methods {{

    public static readonly Dictionary<string, {enum_name}> FromCURIE = new Dictionary<string, {enum_name}>(
        (({enum_name}[])Enum.GetValues(typeof({enum_name}))).Select((v) => new KeyValuePair<string, {enum_name}>(v.CURIE(), v))
    );

""")
while queue:
    key = queue.popleft()
    term = cv[key]
    name = "".join(
        [t.title().replace("/", "").replace("-", "") for t in term.name.split(" ")]
    )
    if name in seen:
        continue
    seen.add(name)
    name_map[name] = term.name
    id_map[name] = term.id
    enum_buf.write(f"    {name},\n")
    queue.extend([t.id for t in term.children])
enum_buf.write("}")
method_buf.write(f"""
    public static string Name(this {enum_name} term) {{
        switch(term) {{
""")
for k, tname in name_map.items():
    method_buf.write(f'           case {enum_name}.{k}: return "{tname}";\n')
method_buf.write("           default: throw new InvalidOperationException();\n")
method_buf.write("      }\n")
method_buf.write("   }\n")
method_buf.write(f"""
    public static string CURIE(this {enum_name} term) {{
        switch(term) {{
""")
for k, tname in id_map.items():
    method_buf.write(f'           case {enum_name}.{k}: return "{tname}";\n')
method_buf.write("           default: throw new InvalidOperationException();\n")
method_buf.write("      }\n")
method_buf.write("   }\n")
method_buf.write("}")

print(enum_buf.getvalue())
print(method_buf.getvalue())