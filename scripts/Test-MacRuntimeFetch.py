import base64
import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest

module = importlib.util.spec_from_file_location('fetch', Path(__file__).with_name('Fetch-MacRuntimes.py'))
fetch = importlib.util.module_from_spec(module)
module.loader.exec_module(fetch)


class RuntimeArchiveTests(unittest.TestCase):
    def fixture(self, root, name='package/bin/tool', symlink=False):
        archive = root / 'runtime.tgz'
        with tarfile.open(archive, 'w:gz') as stream:
            entry = tarfile.TarInfo(name)
            entry.mode = 0o755
            if symlink:
                entry.type = tarfile.SYMTYPE
                entry.linkname = '/tmp/unrelated'
                stream.addfile(entry)
            else:
                entry.size = 4
                stream.addfile(entry, io.BytesIO(b'tool'))
        spec = dict(archivePrefix='package/', archiveSha512=base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode())
        return archive, spec

    def test_verified_archive_extracts_exact_bytes(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            archive, spec = self.fixture(root)
            fetch.extract(archive, root / 'out', spec)
            self.assertEqual((root / 'out/bin/tool').read_bytes(), b'tool')

    def test_hash_mismatch_writes_no_files(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            archive, spec = self.fixture(root)
            spec['archiveSha512'] = 'invalid'
            with self.assertRaises(ValueError):
                fetch.extract(archive, root / 'out', spec)
            self.assertFalse((root / 'out').exists())

    def test_traversal_and_links_are_rejected(self):
        for name, link in [('package/../escape', False), ('package/bin/link', True), ('package/C:/escape', False)]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as work:
                root = Path(work)
                archive, spec = self.fixture(root, name, link)
                with self.assertRaises(ValueError):
                    fetch.extract(archive, root / 'out', spec)
                self.assertFalse((root / 'escape').exists())


if __name__ == '__main__':
    unittest.main()
