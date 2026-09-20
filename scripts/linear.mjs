#!/usr/bin/env node
// linear.mjs — minimal Linear helper (no dependencies, Node 18+).
// Reads LINEAR_API_KEY from env or <repoRoot>/.env.local.
//
// Usage:
//   node scripts/linear.mjs whoami
//   node scripts/linear.mjs teams
//   node scripts/linear.mjs projects
//   node scripts/linear.mjs project-create <name> --team <teamId>
//   node scripts/linear.mjs states --team <teamId>
//   node scripts/linear.mjs create --project <name|id> --title <t> [--desc <d>] [--state <name>]
//   node scripts/linear.mjs list --project <name|id>
//   node scripts/linear.mjs comment <issueId> <body...>
//   node scripts/linear.mjs state <issueId> <stateName>
//   node scripts/linear.mjs delete <issueId>
//   node scripts/linear.mjs raw '<graphql>' [--var key=value]
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const API = 'https://api.linear.app/graphql';

function loadEnv() {
  const f = join(ROOT, '.env.local');
  if (!existsSync(f)) return;
  for (const line of readFileSync(f, 'utf8').split('\n')) {
    const m = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/);
    if (!m || m[1].startsWith('#')) continue;
    if (!(m[1] in process.env)) process.env[m[1]] = m[2].replace(/^["']|["']$/g, '');
  }
}

async function gql(query, variables = {}) {
  const key = process.env['LINEAR_API_KEY'];
  if (!key) {
    console.error('LINEAR_API_KEY missing (env or .env.local)');
    process.exit(1);
  }
  let lastErr = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      const res = await fetch(API, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: key },
        body: JSON.stringify({ query, variables }),
      });
      const text = await res.text();
      const json = JSON.parse(text);
      if (json.errors?.length) {
        console.error('Linear API error:', JSON.stringify(json.errors, null, 2));
        process.exit(1);
      }
      return json.data;
    } catch (e) {
      lastErr = e;
      if (attempt < 3) await new Promise((r) => setTimeout(r, 2000 * attempt));
    }
  }
  console.error('Linear API failed after 3 attempts:', lastErr?.message ?? lastErr);
  process.exit(1);
}

function parseArgs(argv) {
  const flags = {};
  const pos = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const eq = a.indexOf('=');
      if (eq > -1) flags[a.slice(2, eq)] = a.slice(eq + 1);
      else flags[a.slice(2)] = argv[++i] ?? '';
    } else pos.push(a);
  }
  return { flags, pos };
}

async function resolveProject(ref) {
  const d = await gql(`{ projects(first: 100) { nodes { id name } } }`);
  const p = d.projects.nodes.find(
    (p) => p.id === ref || p.name.toLowerCase() === String(ref).toLowerCase(),
  );
  if (!p) {
    console.error(`Project not found: ${ref}`);
    process.exit(1);
  }
  return p;
}

async function resolveTeamOfProject(projectId) {
  const d = await gql(`query($id: String!) { project(id: $id) { teams(first: 1) { nodes { id } } } }`, {
    id: projectId,
  });
  return d.project.teams.nodes[0]?.id;
}

async function resolveState(teamId, name) {
  const d = await gql(
    `query($t: WorkflowStateFilter!) { workflowStates(filter: $t, first: 50) { nodes { id name } } }`,
    { t: { team: { id: { eq: teamId } } } },
  );
  const s = d.workflowStates.nodes.find((s) => s.name.toLowerCase() === name.toLowerCase());
  if (!s) {
    console.error(
      `State not found: ${name} (available: ${d.workflowStates.nodes.map((s) => s.name).join(', ')})`,
    );
    process.exit(1);
  }
  return s;
}

const [cmd, ...rest] = process.argv.slice(2);
const { flags, pos } = parseArgs(rest);
loadEnv();

switch (cmd) {
  case 'whoami': {
    const d = await gql('{ viewer { id name email } organization { name urlKey } }');
    console.log(`${d.viewer.name} <${d.viewer.email}> @ ${d.organization.name} (${d.organization.urlKey})`);
    break;
  }
  case 'teams': {
    const d = await gql('{ teams(first: 50) { nodes { id key name } } }');
    for (const t of d.teams.nodes) console.log(`${t.key}\t${t.name}\t${t.id}`);
    break;
  }
  case 'projects': {
    const d = await gql('{ projects(first: 100) { nodes { id name url } } }');
    for (const p of d.projects.nodes) console.log(`${p.name}\t${p.url}\t${p.id}`);
    break;
  }
  case 'project-create': {
    const name = pos[0];
    if (!name || !flags['team']) {
      console.error('Usage: project-create <name> --team <teamId>');
      process.exit(1);
    }
    const d = await gql(
      `mutation($i: ProjectCreateInput!) { projectCreate(input: $i) { success project { id name url } } }`,
      { i: { name, teamIds: [flags['team']] } },
    );
    console.log(`${d.projectCreate.project.name}\t${d.projectCreate.project.url}\t${d.projectCreate.project.id}`);
    break;
  }
  case 'states': {
    if (!flags['team']) {
      console.error('Usage: states --team <teamId>');
      process.exit(1);
    }
    const d = await gql(
      `query($t: WorkflowStateFilter!) { workflowStates(filter: $t, first: 50) { nodes { id name type } } }`,
      { t: { team: { id: { eq: flags['team'] } } } },
    );
    for (const s of d.workflowStates.nodes) console.log(`${s.name}\t${s.type}\t${s.id}`);
    break;
  }
  case 'create': {
    if (!flags['project'] || !flags['title']) {
      console.error('Usage: create --project <name|id> --title <t> [--desc <d>] [--state <name>]');
      process.exit(1);
    }
    const p = await resolveProject(flags['project']);
    const teamId = await resolveTeamOfProject(p.id);
    const input = { teamId, projectId: p.id, title: flags['title'], description: flags['desc'] ?? '' };
    if (flags['state']) input.stateId = (await resolveState(teamId, flags['state'])).id;
    const d = await gql(
      `mutation($i: IssueCreateInput!) { issueCreate(input: $i) { success issue { id identifier title url } } }`,
      { i: input },
    );
    const iss = d.issueCreate.issue;
    console.log(`${iss.identifier}\t${iss.title}\n${iss.url}\nid: ${iss.id}`);
    break;
  }
  case 'list': {
    if (!flags['project']) {
      console.error('Usage: list --project <name|id>');
      process.exit(1);
    }
    const p = await resolveProject(flags['project']);
    const d = await gql(
      `query($f: IssueFilter!) { issues(filter: $f, first: 50) { nodes { id identifier title state { name } url } } }`,
      { f: { project: { id: { eq: p.id } } } },
    );
    for (const i of d.issues.nodes) console.log(`${i.identifier}\t[${i.state.name}]\t${i.title}\t${i.id}`);
    break;
  }
  case 'comment': {
    const [id, ...body] = pos;
    if (!id || !body.length) {
      console.error('Usage: comment <issueId> <body...>');
      process.exit(1);
    }
    await gql(`mutation($i: CommentCreateInput!) { commentCreate(input: $i) { success } }`, {
      i: { issueId: id, body: body.join(' ') },
    });
    console.log('comment added');
    break;
  }
  case 'state': {
    const [id, name] = pos;
    if (!id || !name) {
      console.error('Usage: state <issueId> <stateName>');
      process.exit(1);
    }
    const issue = await gql(`query($id: String!) { issue(id: $id) { team { id } } }`, { id });
    const s = await resolveState(issue.issue.team.id, name);
    await gql(`mutation($id: String!, $i: IssueUpdateInput!) { issueUpdate(id: $id, input: $i) { success } }`, {
      id,
      i: { stateId: s.id },
    });
    console.log(`moved to ${s.name}`);
    break;
  }
  case 'find': {
    // find --project <name|id> --title <prefix> → prints issue id or empty
    if (!flags['project'] || !flags['title']) {
      console.error('Usage: find --project <name|id> --title <prefix>');
      process.exit(1);
    }
    const p = await resolveProject(flags['project']);
    // List-then-filter instead of searchIssues: Linear's text search does not
    // reliably match bracketed prefixes like "[PR #8]" in titles.
    const d = await gql(
      `query($f: IssueFilter!) { issues(filter: $f, first: 250) { nodes { id title } } }`,
      { f: { project: { id: { eq: p.id } } } },
    );
    const hit = d.issues.nodes.find((n) => n.title.startsWith(flags['title']));
    if (hit) console.log(hit.id);
    break;
  }
  case 'find-key': {
    // find-key STU-123 → prints issue id or empty
    if (!pos[0]) {
      console.error('Usage: find-key <ISSUE-KEY>');
      process.exit(1);
    }
    const d = await gql(
      `query($q: String!) { searchIssues(term: $q, first: 10) { nodes { id identifier } } }`,
      { q: pos[0] },
    );
    const hit = d.searchIssues.nodes.find(
      (n) => n.identifier.toLowerCase() === pos[0].toLowerCase(),
    );
    if (hit) console.log(hit.id);
    break;
  }
  case 'delete': {
    if (!pos[0]) {
      console.error('Usage: delete <issueId>');
      process.exit(1);
    }
    await gql(`mutation($id: String!) { issueDelete(id: $id) { success } }`, { id: pos[0] });
    console.log('deleted');
    break;
  }
  case 'raw': {
    const vars = {};
    const varFlags = Array.isArray(flags['var']) ? flags['var'] : flags['var'] ? [flags['var']] : [];
    for (const v of varFlags) {
      const eq = v.indexOf('=');
      vars[v.slice(0, eq)] = v.slice(eq + 1);
    }
    console.log(JSON.stringify(await gql(pos.join(' '), vars), null, 2));
    break;
  }
  default:
    console.error(
      'Commands: whoami, teams, projects, project-create, states, create, list, comment, state, delete, raw',
    );
    process.exit(1);
}

