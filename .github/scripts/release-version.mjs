const tag = process.argv[2] ?? '';
const number = '(?:0|[1-9][0-9]*)';
const identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)';
const releaseTag = new RegExp(`^v${number}\\.${number}\\.${number}(?:-${identifier}(?:\\.${identifier})*)?$`);
if (tag.length > 128 || !releaseTag.test(tag)) {
  console.error('Release tag must be vMAJOR.MINOR.PATCH with an optional prerelease suffix, at most 128 characters, without build metadata.');
  process.exit(1);
}
console.log(tag.slice(1));
