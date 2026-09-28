#!/usr/bin/env python3
"""Compares PaspanCodeGraphMcp with a Roslyn-based MCP server (ZuCSharpMcp) over MCP on one solution.

Both servers get the same calls: workspace_load, then find_references for a sample of the solution's types and
members (outside test files). The script reports load time, time to the first answer, time per query, memory
(resident set, from /proc, so Linux only) and how the references agree, taking the Roslyn server as the truth.

    python3 compare.py Your.slnx --zu "dotnet /path/zu-csharp-mcp.dll" [--paspan paspan-code-graph-mcp]
        [--native /path/native/paspan-code-graph-mcp] [--sample 300]
"""
import argparse, json, os, random, shlex, subprocess, tempfile, threading, time


class Server:
    """A stdio MCP client, just enough for tools/call."""

    def __init__(self, command):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        self.errors = []
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        self.next_id = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "compare", "version": "1"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.next_id += 1
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params})
        while True:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("server exited: " + "".join(self.errors[-20:]))
            message = json.loads(line)
            if message.get("id") == self.next_id:
                return message

    def call(self, tool, **arguments):
        result = self.request("tools/call", {"name": tool, "arguments": arguments}).get("result", {})
        text = "".join(c.get("text", "") for c in result.get("content", []))
        try:
            return json.loads(text), result.get("isError", False)
        except ValueError:
            return text, True

    def memory(self):
        status = open(f"/proc/{self.process.pid}/status").read()
        value = lambda key: int(next(l for l in status.splitlines() if l.startswith(key)).split()[1]) // 1024
        return {"rssMB": value("VmRSS:"), "peakMB": value("VmHWM:")}

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(10)
        except subprocess.TimeoutExpired:
            self.process.kill()


def references(server, solution, symbol, roslyn, related):
    """The (file, line, column) of the references find_references lists: exact and inferred, not name-only."""
    result, error = server.call("find_references", symbol=symbol, maxResults=5000, includeImplementations=related)
    if error or not isinstance(result, dict) or "files" not in result:
        return None
    found = set()
    for group in result["files"]:
        for item in group["items"]:
            if item.get("confidence") == "NameOnly" or (roslyn and item.get("isImplicit")):
                continue
            found.add((os.path.relpath(group["file"], os.path.dirname(solution)), item["line"], item["column"]))
    return found


def sample_symbols(server, count):
    ids = set()
    for kind in ["Class", "Interface", "Struct", "Record", "Enum", "Method", "Constructor", "Property", "Field", "Event"]:
        result, _ = server.call("find_symbol", query="*", kind=kind, maxResults=100000)
        ids.update(item["id"] for item in result.get("items", []) if not item["location"]["isTest"])
    random.seed(7)
    return sorted(random.sample(sorted(ids), min(count, len(ids))))


def measure(command, solution, ids, roslyn):
    server = Server(command)
    start = time.time()
    server.call("workspace_load", path=solution)
    load = time.time() - start
    after_load = server.memory()
    if ids is None:
        ids = sample_symbols(server, arguments.sample)
    start = time.time()
    answers = {ids[0]: references(server, solution, ids[0], roslyn, True)}
    first = time.time() - start
    start = time.time()
    for symbol in ids[1:]:
        answers[symbol] = references(server, solution, symbol, roslyn, True)
    per_query = (time.time() - start) / max(1, len(ids) - 1)
    after_queries = server.memory()
    for symbol in ids:
        answers["direct " + symbol] = references(server, solution, symbol, roslyn, False)
    server.close()
    timing = {"loadSeconds": round(load, 2), "firstQuerySeconds": round(first, 2), "perQueryMs": round(1000 * per_query, 1),
              "afterLoad": after_load, "afterQueries": after_queries}
    return timing, ids, answers


def agreement(ids, ours, theirs, prefix="", members=None):
    both = only_ours = only_theirs = skipped = 0
    for symbol in ids:
        if members is not None and symbol.startswith("T:") == members:
            continue
        a, b = ours.get(prefix + symbol), theirs.get(prefix + symbol)
        if a is None or b is None:
            skipped += 1
            continue
        both += len(a & b)
        only_ours += len(a - b)
        only_theirs += len(b - a)
    return {"both": both, "onlyPaspan": only_ours, "onlyRoslyn": only_theirs, "skipped": skipped,
            "precision": round(both / max(1, both + only_ours), 4), "recall": round(both / max(1, both + only_theirs), 4)}


parser = argparse.ArgumentParser()
parser.add_argument("solution")
parser.add_argument("--zu", required=True, help="command that starts the Roslyn server")
parser.add_argument("--paspan", default="paspan-code-graph-mcp", help="command that starts this server")
parser.add_argument("--native", help="path of the NativeAOT binary, also measured")
parser.add_argument("--sample", type=int, default=300)
arguments = parser.parse_args()
solution = os.path.abspath(arguments.solution)
paspan = shlex.split(arguments.paspan) + ["--no-watch"]

with tempfile.TemporaryDirectory() as directory:
    cache = os.path.join(directory, "graph.bin")
    report = {"solution": solution}
    report["paspan"], ids, ours = measure(paspan + ["--cache", cache], solution, None, False)
    report["paspanFromCache"], _, _ = measure(paspan + ["--cache", cache], solution, ids[:1], False)
    if arguments.native:
        report["paspanNative"], _, native = measure([arguments.native, "--no-watch", "--no-cache"], solution, ids, False)
        report["nativeAnswersSame"] = native == ours
    report["roslyn"], _, theirs = measure(shlex.split(arguments.zu) + ["--no-watch", "--no-cache"], solution, ids, True)

report["symbols"] = len(ids)
report["references"] = agreement(ids, ours, theirs)
report["typeReferences"] = agreement(ids, ours, theirs, members=False)
report["memberReferences"] = agreement(ids, ours, theirs, members=True)
report["directMemberReferences"] = agreement(ids, ours, theirs, "direct ", members=True)
print(json.dumps(report, indent=1))
