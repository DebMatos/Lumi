const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { setup, deferred, streamed, reply } = require('./chat-harness.cjs');
const flush = () => new Promise(resolve => setImmediate(resolve));
const history = [
  { id: '1', role: 'user', content: 'Pergunta anterior' },
  { id: '2', role: 'assistant', content: 'Resposta anterior' }
];
const displayed = h => h.get('#messages').children.map(element => [element.className, element.textContent]);
function unlocked(h) {
  assert.equal(h.get('#sendButton').disabled, false);
  assert.equal(h.get('#micButton').disabled, false);
  assert.equal(h.get('#status').textContent, 'Inativo');
}

test('initial history loads automatically, preserves the draft and unlocks controls', async () => {
  const h = await setup({ pendingHistory: true });
  assert.equal(h.historyRequests.length, 1);
  assert.equal(h.historyRequests[0].url, `/conversations/${h.localStorage.getItem('lumi-session')}/messages`);
  assert.equal(h.get('#sendButton').disabled, true);
  assert.equal(h.get('#micButton').disabled, true);
  assert.match(h.get('#status').textContent, /A carregar histórico/);
  assert.equal(h.get('#status').hidden, false);
  h.get('#messageInput').value = 'rascunho';
  h.historyRequests[0].resolve({ ok: true, json: async () => history });
  await flush();
  assert.deepEqual(displayed(h), [['message user', 'Pergunta anterior'], ['message lumi', 'Resposta anterior']]);
  assert.equal(h.get('#messageInput').value, 'rascunho');
  assert.equal(h.get('#error').textContent, '');
  unlocked(h);
});

test('404 history means an empty conversation and allows sending', async () => {
  const h = await setup({ pendingHistory: true });
  h.historyRequests[0].resolve({ ok: false, status: 404 });
  await flush();
  unlocked(h);
  assert.deepEqual(displayed(h), []);
  assert.equal(h.get('#error').textContent, '');
  await reply(h);
  assert.deepEqual(displayed(h), [['message user', 'pergunta'], ['message lumi', 'Olá']]);
});

test('network, HTTP and JSON failures release the history lock and allow sending', async () => {
  for (const failure of ['network', 'http', 'json']) {
    const h = await setup({ pendingHistory: true });
    const request = h.historyRequests[0];
    if (failure === 'network') request.reject(new Error('offline'));
    if (failure === 'http') request.resolve({ ok: false, status: 503 });
    if (failure === 'json') request.resolve({ ok: true, json: async () => { throw new Error('bad JSON'); } });
    await flush();
    unlocked(h);
    assert.match(h.get('#error').textContent, /Não foi possível recuperar o histórico/);
    await reply(h);
    assert.equal(h.get('#messages').children.length, 2);
  }
});

test('sending and recording stay blocked until the history body finishes loading', async () => {
  const h = await setup({ pendingHistory: true });
  const body = deferred();
  h.historyRequests[0].resolve({ ok: true, json: () => body.promise });
  await flush();
  h.get('#messageInput').value = 'mensagem entretanto';
  await h.get('#chatForm').fire('submit');
  await h.get('#micButton').fire('click');
  assert.equal(h.requests.length, 0);
  assert.equal(h.tracks.length, 0);
  assert.equal(h.get('#messageInput').value, 'mensagem entretanto');
  assert.deepEqual(displayed(h), []);
  body.resolve(history);
  await flush();
  unlocked(h);
  const done = h.get('#chatForm').fire('submit');
  h.requests[0].resolve(streamed('Resposta nova'));
  await done;
  assert.deepEqual(displayed(h), [
    ['message user', 'Pergunta anterior'], ['message lumi', 'Resposta anterior'],
    ['message user', 'mensagem entretanto'], ['message lumi', 'Resposta nova']
  ]);
});

test('new conversation aborts history and ignores late fetch, JSON and error completions', async () => {
  for (const completion of ['fetch', 'json', 'error']) {
    const h = await setup({ pendingHistory: true });
    const old = h.historyRequests[0];
    const body = deferred();
    if (completion === 'json') {
      old.resolve({ ok: true, json: () => body.promise });
      await flush();
    }
    h.get('#messageInput').value = 'nova conversa';
    h.get('#newChat').fire('click');
    assert.equal(old.options.signal.aborted, true);
    unlocked(h);
    assert.equal(h.get('#messageInput').value, 'nova conversa');
    const done = h.get('#chatForm').fire('submit');
    if (completion === 'fetch') old.resolve({ ok: true, json: async () => history });
    if (completion === 'json') body.resolve(history);
    if (completion === 'error') old.reject(new Error('old request failed'));
    await flush();
    assert.deepEqual(displayed(h), [['message user', 'nova conversa'], ['message lumi', '']]);
    assert.equal(h.get('#error').textContent, '');
    assert.equal(h.get('#sendButton').disabled, true);
    assert.equal(h.get('#status').textContent, 'A gerar resposta');
    h.requests[0].resolve(streamed('Resposta nova'));
    await done;
    unlocked(h);
    assert.deepEqual(displayed(h), [['message user', 'nova conversa'], ['message lumi', 'Resposta nova']]);
  }
});

test('overlapping initial history calls do not duplicate requests or rendered messages', async () => {
  const h = await setup({ pendingHistory: true });
  void vm.runInContext('loadConversationHistory()', h.context);
  assert.equal(h.historyRequests.length, 1);
  h.historyRequests[0].resolve({ ok: true, json: async () => history });
  await flush();
  await reply(h, 'Resposta nova');
  assert.deepEqual(displayed(h), [
    ['message user', 'Pergunta anterior'], ['message lumi', 'Resposta anterior'],
    ['message user', 'pergunta'], ['message lumi', 'Resposta nova']
  ]);
});

test('an old history finally cannot unlock a newer session history load', async () => {
  const h = await setup({ pendingHistory: true });
  const old = h.historyRequests[0];
  h.get('#newChat').fire('click');
  const current = vm.runInContext('loadConversationHistory()', h.context);
  old.resolve({ ok: true, json: async () => history });
  await flush();
  assert.equal(h.get('#sendButton').disabled, true);
  assert.equal(h.get('#micButton').disabled, true);
  assert.deepEqual(displayed(h), []);
  h.historyRequests[1].resolve({ ok: false, status: 404 });
  await current;
  unlocked(h);
});
