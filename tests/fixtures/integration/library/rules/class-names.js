// Class names longer than 20 characters are hard to read in the generated code.
const naming = maquettiste.use('naming');

maquettiste.rule({
  id: 'class-name-length',
  severity: 'error',
  kinds: ['entity'],
  check(element, model, report) {
    const name = naming.class_name(element);
    if (name.length > 20) report(`The class name ${name} has ${name.length} characters; keep it to 20.`, { pointer: '/name' });
  },
});
