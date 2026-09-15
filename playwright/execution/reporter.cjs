const fs = require('node:fs');
const path = require('node:path');
module.exports = class ExecutionReporter {
  tests = [];
  errors = 0;
  onError() { this.errors++; }
  onTestEnd(test, result) {
    const annotated = test.annotations.find(item => item.type === 'quality-failure')?.description;
    const classification = result.status === 'passed' ? 'None' : ['NeedsReview', 'TestFailure', 'InfrastructureFailure'].includes(annotated) ? annotated : 'InfrastructureFailure';
    this.tests.push({ id: test.title, status: result.status, classification });
  }
  onEnd(result) {
    fs.writeFileSync(path.join(__dirname, 'summary.json'), JSON.stringify({ status: result.status, tests: this.tests, errors: this.errors }));
  }
};
