# TestFramework.Container.Web.SampleSite

An Angular application used as a container test fixture: two routed pages and an orders list that
reads `assets/config.json` at run time to find its backend — same-origin behind the site container's
proxy when `apiBaseUrl` is `null`, or the absolute address the environment wrote into the file.

It is a plain folder, not a project the solution builds. Build it manually or in the pipeline:

```bash
npm ci && npm run build
```

The output lands in `dist/sample-site/browser`. Tests that need it skip with a reason while it is
absent; the tests that run npm themselves are additionally gated behind
`TESTFRAMEWORK_CONTAINER_SITE_NPM=1`.
