// 本地抓包/回放服务器：捕获 claude CLI 的原始请求并返回 200
const http = require('http');
const fs = require('fs');
const port = process.env.CAPTURE_PORT || 5060;
const server = http.createServer((req, res) => {
    let chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => {
        const body = Buffer.concat(chunks).toString('utf8');
        const record = {
            method: req.method,
            url: req.url,
            headers: req.headers,
            body: body
        };
        fs.appendFileSync(process.env.CAPTURE_FILE || 'D:\\bs\\code\\others\\Claude-Agent\\investigation\\cli_capture.jsonl',
            JSON.stringify(record, null, 2) + '\n---\n');
        // 返回一个最小合法 Anthropic 响应
        const resp = {
            id: 'msg_capture',
            type: 'message',
            role: 'assistant',
            model: 'claude-haiku-5-5',
            content: [{ type: 'text', text: 'OK' }],
            stop_reason: 'end_turn',
            stop_sequence: null,
            usage: { input_tokens: 1, output_tokens: 1 }
        };
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify(resp));
    });
});
server.listen(port, '127.0.0.1', () => console.log(`capture server on ${port}`));
