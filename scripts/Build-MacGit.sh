#!/bin/sh
# Build-time CLT and curl are required; the resulting Git needs only macOS libraries.
set -eu
if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
  echo 'Build-MacGit requires a native arm64 Mac.' >&2; exit 2
fi
output=${1:?Usage: Build-MacGit.sh NEW_ABSOLUTE_OUTPUT_DIRECTORY}
case "$output" in /*) ;; *) echo 'Use an absolute output directory.' >&2; exit 2;; esac
if [ -e "$output" ]; then echo 'Output already exists; choose a new directory.' >&2; exit 2; fi
mkdir -p "$output"
cd "$output"
version=2.54.0
archive="git-$version.tar.xz"
expected=f689162364c10de79ef89aa8dbf48731eb057e34edbbd20aca510ce0154681a3
curl --fail --location --retry 2 "https://www.kernel.org/pub/software/scm/git/$archive" -o "$archive"
actual=$(shasum -a 256 "$archive" | cut -d ' ' -f 1)
if [ "$actual" != "$expected" ]; then echo 'Git source hash mismatch.' >&2; exit 3; fi
tar -xf "$archive"
cd "git-$version"
make -j4 prefix=/usr/local/agentdock-git RUNTIME_PREFIX=YesPlease NO_GETTEXT=YesPlease NO_PERL=YesPlease NO_TCLTK=YesPlease NO_GITWEB=YesPlease NO_PYTHON=YesPlease NO_OPENSSL=YesPlease NO_HOMEBREW=YesPlease NO_DARWIN_PORTS=YesPlease CFLAGS='-O2 -mmacosx-version-min=14.0' all > "$output/build.log" 2>&1
make prefix=/usr/local/agentdock-git RUNTIME_PREFIX=YesPlease NO_GETTEXT=YesPlease NO_PERL=YesPlease NO_TCLTK=YesPlease NO_GITWEB=YesPlease NO_PYTHON=YesPlease NO_OPENSSL=YesPlease NO_HOMEBREW=YesPlease NO_DARWIN_PORTS=YesPlease CFLAGS='-O2 -mmacosx-version-min=14.0' DESTDIR="$output/stage" install >> "$output/build.log" 2>&1
cp COPYING "$output/stage/usr/local/agentdock-git/COPYING"
"$output/stage/usr/local/agentdock-git/bin/git" --version
otool -L "$output/stage/usr/local/agentdock-git/bin/git"
printf '%s\n' "source=https://www.kernel.org/pub/software/scm/git/$archive" "sha256=$expected" 'build=macOS arm64; Apple CLT; runtime prefix; no Homebrew/Perl/Tcl/Tk/gettext/OpenSSL' > "$output/provenance.txt"
