import os
import unittest
from unittest.mock import patch, Mock
import urllib.request
import server


class ProviderRoutingTests(unittest.TestCase):
    def test_exact_domestic_host_direct_without_explicit_proxy(self):
        opener = Mock()
        with patch.dict(os.environ, {}, clear=True), \
             patch.object(server.urllib.request, 'build_opener', return_value=opener), \
             patch.object(server.urllib.request, 'urlopen') as system:
            request = urllib.request.Request('https://api.siliconflow.cn/v1/models')
            server.provider_urlopen(request, timeout=10)
            opener.open.assert_called_once_with(request, timeout=10)
            system.assert_not_called()

    def test_other_hosts_and_explicit_proxy_keep_system_routing(self):
        for endpoint, env in (
            ('https://api.siliconflow.cn/v1/models', {'HTTPS_PROXY': 'http://127.0.0.1:9999'}),
            ('https://api.siliconflow.cn.other.test/v1/models', {}),
            ('https://example.test/v1/models', {}),
        ):
            with patch.dict(os.environ, env, clear=True), \
                 patch.object(server.urllib.request, 'build_opener') as direct, \
                 patch.object(server.urllib.request, 'urlopen') as system:
                request = urllib.request.Request(endpoint)
                server.provider_urlopen(request, timeout=10)
                direct.assert_not_called()
                system.assert_called_once_with(request, timeout=10)
