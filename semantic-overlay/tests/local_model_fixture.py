"""Loopback-only model fixture for desktop soak tests; never contacts a provider."""
import json
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        messages = data.get('messages', [])
        system = str(messages[0].get('content', '')) if messages else ''
        if 'explanation' in system:
            content = {'explanation': '这是压力测试使用的模拟中文解释。', 'entities': []}
        else:
            text = str(messages[1].get('content', '')) if len(messages) > 1 else ''
            terms = r'OneAPI|bootcamp|latency|transcription|Kubernetes|rollback|semantic overlay'
            entities = [{'text': match.group(), 'type': 'concept', 'start': match.start(), 'end': match.end()}
                        for match in re.finditer(terms, text, re.IGNORECASE)]
            content = {'entities': entities, 'actions': [], 'ok': True}
        body = json.dumps({'choices': [{'message': {'content': json.dumps(content)}}]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    ThreadingHTTPServer(('127.0.0.1', 18879), Handler).serve_forever()
