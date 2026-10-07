# Snowball French reference vocabulary

`voc.txt` and `output.txt` are the sample French vocabulary and its stemmed equivalent published
with the Snowball French stemming algorithm:

- <https://snowballstem.org/algorithms/french/stemmer.html> — "Links to resources" lists
  *Sample French vocabulary* and *Its stemmed equivalent*.

Both files are copied verbatim (md5 `06a0d02a8e859679e5a8f421eea132ca` and
`5cd2b41e851b06d4bed1654d92db26f8`, 21,653 lines each, one word per line, one stem per line,
aligned). `FrenchStemmerTests` stems every word of `voc.txt` and asserts the result is the
matching line of `output.txt`, which is the conformance claim
`LexiSharp.Linguistics.FrenchStemmer` makes in its documentation.

Snowball's own data repository is <https://github.com/snowballstem/snowball-data>, where these
files live as `french/voc.txt` and `french/output.txt`.

Licensing: the Snowball project distributes the compiler, the languages and their test data under
a BSD-style licence — the project states the encodings of the algorithms may be used free of
charge for any purpose. The vocabulary files are data alongside the algorithm they exercise.

Why a reference exists here at all: `PorterStemmer` has had one since it was written, and a
stemmer that cannot be checked against a reference can only claim to be *a* stemmer, not *the*
one it names. Two files are what turns that claim into a test.
