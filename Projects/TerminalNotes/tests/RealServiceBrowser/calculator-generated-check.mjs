// Independent browser checks for the real OpenCode-generated calculator.
// The harness opens the generated service and never writes its application code.
export default async function ({ evaluate, key, click, captureScreenshot }) {
  const checks = [];
  const read = selector => evaluate(selector => document.querySelector(selector)?.textContent.trim(), selector);
  const record = (name, actual, expected) => checks.push({ name, actual, expected, passed: actual === expected });
  const type = async text => { for (const character of text) await key(character); };
  const calculate = async (text, expected) => {
    await key('Escape');
    await type(text);
    await key('Enter');
    record(`keyboard ${text}`, await read('#result'), expected);
  };
  record('Korean title', await evaluate(() => document.title), '계산기');
  await calculate('12+7', '19');
  await calculate('9-14', '-5');
  await calculate('6*7', '42');
  await calculate('7/2', '3.5');
  await calculate('2+3*4', '14');
  await calculate('0.1+0.2', '0.3');
  await calculate('-5+3', '-2');
  await calculate('2*-3', '-6');
  await key('Escape');
  await type('9/0');
  await key('Enter');
  record('zero division message', await read('#error'), '0으로 나눌 수 없습니다');
  await click('[data-key="clear"]');
  await click('[data-key="7"]');
  await click('[data-key="*"]');
  await click('[data-key="2"]');
  await click('[data-key="equals"]');
  record('mouse 7 × 2 and error recovery', await read('#result'), '14');
  await key('Escape');
  await type('123');
  record('expression while typing', await read('#expression'), '123');
  await key('Backspace');
  record('Backspace updates expression', await read('#expression'), '12');
  await key('Enter');
  record('Backspace changes calculation', await read('#result'), '12');
  await type('+3');
  await key('Enter');
  record('continue after equals', await read('#result'), '15');
  await key('4');
  await key('Enter');
  record('new number after equals', await read('#result'), '4');
  await click('[data-key="clear"]');
  record('clear resets result', await read('#result'), '0');
  record('clear resets expression', await read('#expression'), '');
  await calculate('12+7', '19');
  await captureScreenshot('calculator-19.png');
  console.log(JSON.stringify({ browserChecks: checks }, null, 2));
  const failures = checks.filter(check => !check.passed);
  if (failures.length) throw new Error('Real browser acceptance failed: ' + JSON.stringify(failures));
  return { passed: checks.length, checks };
}
