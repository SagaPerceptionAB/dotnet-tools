<script setup lang="ts">
import { ref } from 'vue';

const users = ref([]);
fetch('/api/admin/users').then((r) => r.json()).then((u) => (users.value = u));
</script>

<template>
  <section>
    <h1>Användare</h1>
    <form>
      <label for="q">Sök</label>
      <input id="q" name="q" type="search" maxlength="40" />
      <button type="submit">Sök användare</button>
    </form>
  </section>
</template>
