# Test files from whipper

These files come from whipper's own tests (https://github.com/whipper-team/whipper,
`whipper/test/`, GPLv3 like Spinarr): cdrdao `.toc` files of real discs, AccurateRip database
answers (`dBAR-*.bin`), cd-paranoia progress output and `cd-paranoia -A` output.

Spinarr's TOC parser, disc IDs, AccurateRip and cd-paranoia code are checked against the values
whipper's tests expect for them, so the C# engine gives the same answers whipper did.
