// node client.js <exe> <outdir> <calls.json>  — minimal MCP stdio test client
const { spawn } = require('child_process'), fs = require('fs'), path = require('path'), rl = require('readline');
const [exe, out, callsFile] = process.argv.slice(2);
const calls = JSON.parse(fs.readFileSync(callsFile, 'utf8'));
const p = spawn(exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'] });
const lines = rl.createInterface({ input: p.stdout }); const waiters = [];
lines.on('line', l => waiters.shift()(JSON.parse(l)));
let n = 0;
const rpc = (method, params = {}) => new Promise(r => { waiters.push(r); p.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: ++n, method, params }) + '\n'); });
(async () => {
  let t = Date.now(); const init = await rpc('initialize', { protocolVersion: '2025-06-18' });
  console.log('init', Date.now() - t, 'ms', init.result.serverInfo);
  p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n');
  const tl = (await rpc('tools/list')).result.tools;
  console.log('tools', tl.map(x => x.name), 'schema chars', JSON.stringify(tl).length);
  for (let i = 0; i < calls.length; i++) {
    const [tool, args] = calls[i]; t = Date.now();
    const r = await rpc('tools/call', { name: tool, arguments: args }); const res = r.result || r;
    console.log(`--- ${tool} ${JSON.stringify(args).slice(0, 90)} ${Date.now() - t}ms isError=${res.isError}`);
    for (const c of res.content || [res]) {
      if (c.type === 'image') { const f = path.join(out, `shot${i}.jpg`); fs.writeFileSync(f, Buffer.from(c.data, 'base64')); console.log('image', f, c.data.length, 'b64 chars'); }
      else console.log(c.text ?? JSON.stringify(c));
    }
  }
  p.stdin.end();
})();
