# Porter reference vocabulary

`voc.txt` and `output.txt` are the sample vocabulary and the expected output published by
Martin Porter with the Porter stemming algorithm:

- <https://www.tartarus.org/~martin/PorterStemmer/> — "here is a *sample vocabulary* (0.19
  megabytes), and the corresponding *output*".

Both files are copied verbatim (md5 `ffca0f6cfb021f864d27e2aa717cefd1` and
`c3683dc1400e03069f3b88ff6da1c68b`, 23,531 lines each). `PorterStemmerTests` stems every word of
`voc.txt` and asserts the result is the matching line of `output.txt`, which is the conformance
claim `LexiSharp.Linguistics.PorterStemmer` makes in its documentation.

Licensing, from the same page: "All these encodings of the algorithm can be used free of charge for
any purpose", and its FAQ restates that the licensing is "never more restrictive than the BSD
License".
