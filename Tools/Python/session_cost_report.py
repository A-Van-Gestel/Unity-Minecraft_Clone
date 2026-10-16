"""Report what Claude Code sessions in this repo cost: main context, turn mix, and subagents.

WHAT IT IS FOR
    Measuring delegation (CLAUDE.md "Delegation to Subagents"): the main conversation re-reads its
    whole context every turn, so a session's cost is dominated by turns x context size. This report
    shows, per session, the main thread's turns, average/peak context and the context-weighted
    share of each turn category (git, docs, compile/wait, code reading, ...), then every subagent
    with its type, model, turns and cost — so a delegated loop can be compared with the main-thread
    turns it replaced.

WHERE IT READS
    ~/.claude/projects/<repo path with every non-alphanumeric character as '-'>/<session>.jsonl
    and <session>/subagents/agent-*.jsonl (+ .meta.json for the agent type). One API response can
    span several transcript lines with the same message id; usage is taken once per id.

COST MODEL (API-price equivalent, USD per million tokens — an estimate, not a bill)
    input / output / cache read; cache writes at 2x input (1 h TTL) or 1.25x input (5 min TTL).
    Haiku 5.5's cache-read price is not in the reference table, so 10% of input is assumed.
    A subscription meters usage differently; use the numbers to RANK, not as dollars owed.

READS   transcript files. WRITES nothing.

RUN
    python Tools/Python/session_cost_report.py                  # most recent session
    python Tools/Python/session_cost_report.py --last 5
    python Tools/Python/session_cost_report.py 56e21cf9 a1cb09e8 # by session id prefix
    python Tools/Python/session_cost_report.py --last 1 --categories
    python Tools/Python/session_cost_report.py --last 20 --agents   # cost per agent type, rollup only

    --agents reads only <session>/subagents/ one level deep: a subagent spawned by a subagent is missed.

EXIT CODES
    0  report printed
    2  no transcripts found
"""
import argparse
import collections
import glob
import json
import os
import re
import sys

PRICES = {  # model id prefix: (input, output, cache read)
    "claude-opus-5-5": (4.00, 20.00, 0.20),
    "claude-sonnet-5-5": (2.00, 10.00, 0.20),
    "claude-haiku-5-5": (0.10, 0.50, 0.01),
    "claude-opus-5": (5.00, 25.00, 0.50),
    "claude-sonnet-5": (2.00, 10.00, 0.20),
    "claude-fable-5": (10.00, 50.00, 1.00),
}
FALLBACK_PRICE = PRICES["claude-opus-5-5"]
WRITE_1H_FACTOR = 2.0
WRITE_5M_FACTOR = 1.25
PER_MILLION = 1_000_000


def projects_dir():
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    mangled = re.sub(r"[^A-Za-z0-9]", "-", repo_root)
    return os.path.join(os.path.expanduser("~"), ".claude", "projects", mangled)


def price_for(model):
    for prefix in sorted(PRICES, key=len, reverse=True):
        if model and model.startswith(prefix):
            return PRICES[prefix]
    return FALLBACK_PRICE


def category(name, tool_input):
    if name in ("Bash", "PowerShell"):
        command = tool_input.get("command", "")
        # Global options may sit between `git` and the subcommand (`git -c color.ui=false diff`, `git --no-pager log`).
        if (re.search(r"\bgit(\s+(-[cC]\s+\S+|--?[\w-]+(=\S+)?))*\s+(commit|add|status|diff|log|show|stash|cherry-pick"
                      r"|checkout|reset|rebase|apply|restore|range-diff)\b", command)
                or re.search(r"(fold_into_commit|stage_hunks|audit_reserialize_guids)\.py", command)):
            return "git"
        if re.search(r"unity (recompile|command editor_status)|until unity|dotnet build", command):
            return "compile/wait"
        if "Validate" in command:
            return "validation"
        if re.search(r"\bunity (command|job)", command):
            return "unity-other"
        if "Documentation/" in command or "check_" in command:
            return "docs"
        if re.search(r"\b(grep|rg|sed -n|cat|head|tail|awk|find|ls|wc)\b", command):
            return "read/search"
        return "shell-other"
    if name in ("Read", "Grep", "Glob") or name.startswith("mcp__codegraph"):
        path = tool_input.get("file_path") or tool_input.get("path") or ""
        return "docs" if "Documentation" in path or path.endswith(".md") else "read/search"
    if name in ("Edit", "Write"):
        path = tool_input.get("file_path", "")
        if "memory" in path:
            return "memory"
        return "docs" if path.endswith(".md") else "edit-code"
    if name == "Agent":
        return "delegate"
    return name


def read_turns(path):
    """One entry per API response: [model, usage, first tool category]."""
    turns = collections.OrderedDict()
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") != "assistant":
                continue
            message = record.get("message") or {}
            entry = turns.setdefault(message.get("id"), [message.get("model"), {}, None])
            if message.get("usage"):
                entry[1] = message["usage"]
            for block in message.get("content") or []:
                if isinstance(block, dict) and block.get("type") == "tool_use" and entry[2] is None:
                    entry[2] = category(block.get("name", ""), block.get("input") or {})
    return [t for t in turns.values() if t[0] != "<synthetic>"]


def context_of(usage):
    return sum(usage.get(k) or 0 for k in ("input_tokens", "cache_read_input_tokens",
                                             "cache_creation_input_tokens"))


def cost_of(model, usage):
    input_price, output_price, read_price = price_for(model)
    creation = usage.get("cache_creation") or {}
    write_1h = creation.get("ephemeral_1h_input_tokens")
    write_5m = creation.get("ephemeral_5m_input_tokens")
    if write_1h is None and write_5m is None:
        write_1h, write_5m = usage.get("cache_creation_input_tokens") or 0, 0
    return ((usage.get("input_tokens") or 0) * input_price
            + (usage.get("output_tokens") or 0) * output_price
            + (usage.get("cache_read_input_tokens") or 0) * read_price
            + (write_1h or 0) * input_price * WRITE_1H_FACTOR
            + (write_5m or 0) * input_price * WRITE_5M_FACTOR) / PER_MILLION


def summarize(turns):
    contexts = [context_of(t[1]) for t in turns]
    return {
        "turns": len(turns),
        "avg_ctx": sum(contexts) / len(contexts) if contexts else 0,
        "max_ctx": max(contexts, default=0),
        "cost": sum(cost_of(t[0], t[1]) for t in turns),
        "models": collections.Counter(t[0] for t in turns).most_common(1)[0][0] if turns else "?",
    }


def agent_summaries(session_path):
    """(meta, summary) per subagent transcript of one session; meta is {} when .meta.json is absent."""
    result = []
    for agent_path in sorted(glob.glob(os.path.join(session_path[:-len(".jsonl")], "subagents", "agent-*.jsonl"))):
        meta_path = agent_path[:-len(".jsonl")] + ".meta.json"
        meta = {}
        if os.path.exists(meta_path):
            with open(meta_path, encoding="utf-8") as handle:
                meta = json.load(handle)
        result.append((meta, summarize(read_turns(agent_path))))
    return result


def rollup(files):
    """Cost per agent type across sessions, plus the main threads; returns {type: totals}."""
    rows = collections.defaultdict(lambda: {"spawns": 0, "turns": 0, "ctx_sum": 0.0, "cost": 0.0,
                                            "models": collections.Counter()})
    main_turns, main_cost = 0, 0.0
    for path in files:
        main = summarize(read_turns(path))
        main_turns += main["turns"]
        main_cost += main["cost"]
        for meta, agent in agent_summaries(path):
            row = rows[meta.get("agentType", "?")]
            row["spawns"] += 1
            row["turns"] += agent["turns"]
            row["ctx_sum"] += agent["avg_ctx"] * agent["turns"]
            row["cost"] += agent["cost"]
            row["models"][agent["models"]] += 1

    agent_cost = sum(r["cost"] for r in rows.values())
    grand = main_cost + agent_cost
    print(f"\nSubagents by type across {len(files)} session(s)")
    if not rows:
        print("    no subagents")
    for agent_type, r in sorted(rows.items(), key=lambda item: -item[1]["cost"]):
        avg_ctx = r["ctx_sum"] / r["turns"] if r["turns"] else 0
        share = 100 * r["cost"] / grand if grand else 0.0
        print(f"    {agent_type:18s} {r['models'].most_common(1)[0][0]:18s} {r['spawns']:3d} spawns "
              f"{r['turns']:5d} turns  ctx avg {avg_ctx / 1e3:4.0f}k  ~${r['cost']:7.3f} "
              f"(~${r['cost'] / r['spawns']:.3f}/spawn)  {share:5.1f}%")
    share = 100 * main_cost / grand if grand else 0.0
    print(f"    {'main threads':18s} {'':18s} {len(files):3d} sess.  {main_turns:5d} turns"
          f"{'':15s}~${main_cost:7.3f}{'':17s}{share:5.1f}%")
    print(f"    total ~${grand:.2f} (API-price equivalent, estimate)")
    return dict(rows)


def report_session(path, show_categories):
    session_id = os.path.basename(path)[:-len(".jsonl")]
    turns = read_turns(path)
    main = summarize(turns)
    print(f"\n=== {session_id[:8]}  main: {main['turns']} turns, ctx avg {main['avg_ctx'] / 1e3:.0f}k "
          f"/ peak {main['max_ctx'] / 1e3:.0f}k, {main['models']}, ~${main['cost']:.2f}")

    if show_categories:
        weights = collections.Counter()
        counts = collections.Counter()
        for model, usage, cat in turns:
            weights[cat or "text"] += cost_of(model, usage)
            counts[cat or "text"] += 1
        for cat, cost in weights.most_common():
            share = 100 * cost / main["cost"] if main["cost"] else 0.0
            print(f"    {cat:14s} {counts[cat]:5d} turns  ~${cost:7.2f}  {share:5.1f}%")

    total = main["cost"]
    for meta, agent in agent_summaries(path):
        total += agent["cost"]
        print(f"    agent {meta.get('agentType', '?'):18s} {agent['models']:18s} {agent['turns']:4d} turns, "
              f"ctx avg {agent['avg_ctx'] / 1e3:4.0f}k, ~${agent['cost']:.2f}  {meta.get('description', '')[:40]}")
    print(f"    session total ~${total:.2f}")
    return total


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Report Claude Code session cost for this repo.")
    parser.add_argument("sessions", nargs="*", help="session id prefixes")
    parser.add_argument("--last", type=int, default=1, help="the N most recent sessions (default 1)")
    parser.add_argument("--categories", action="store_true", help="break main turns down by category")
    parser.add_argument("--agents", action="store_true",
                        help="only a rollup of subagent cost per agent type across the selected sessions")
    args = parser.parse_args()
    if args.last < 1:
        parser.error("--last must be at least 1")

    files = sorted(glob.glob(os.path.join(projects_dir(), "*.jsonl")), key=os.path.getmtime)
    if args.sessions:
        files = [f for f in files if any(os.path.basename(f).startswith(s) for s in args.sessions)]
    else:
        files = files[-args.last:]
    if not files:
        print(f"no transcripts found under {projects_dir()}")
        return 2
    if args.agents:
        rollup(files)
        return 0
    grand = sum(report_session(f, args.categories) for f in files)
    if len(files) > 1:
        print(f"\nAll {len(files)} session(s): ~${grand:.2f} (API-price equivalent, estimate)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
