<script setup lang="ts">
import { reactive } from 'vue';

const form = reactive({ email: '', password: '' });

async function submit() {
  await fetch('/api/auth/login', { method: 'POST', body: JSON.stringify(form) });
}
</script>

<template>
  <main>
    <h1>Logga in</h1>
    <form @submit.prevent="submit">
      <label for="email">E-post</label>
      <input id="email" v-model="form.email" type="email" required />

      <label for="password">Lösenord</label>
      <input id="password" v-model="form.password" type="password" required minlength="8" />

      <button type="submit">Logga in</button>
    </form>
  </main>
</template>
